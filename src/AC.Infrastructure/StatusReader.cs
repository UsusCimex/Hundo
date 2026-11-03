using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

/// <summary>
/// Reads which achievements the player has: from the Web API (a key and public "Game details") or through the Steam
/// client helper (any profile, but Steam shows the user in the game for a second). Each read returns the newly unlocked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StatusReader(SteamWebApi api, BridgeClient bridge, Games games, GlobalPercentages percentages, AppSettings settings)
{
    /// <summary>From the source the settings choose; without a key or a SteamID, through the Steam client.</summary>
    public async Task<IReadOnlyList<string>> ReadAsync(GameData game, CancellationToken ct = default)
    {
        game.StatProgress = SteamLocal.ReadProgress(game.AppId, settings.SteamId64);
        bool useClient = settings.StatusSource == StatusSource.SteamClient || !settings.HasApiKey || settings.SteamId64 == 0;
        if (!useClient) return await ReadWebApiAsync(game, ct);
        var (fresh, error) = await ReadViaClientAsync(game, ct);
        if (error != null) throw new SteamApiException(Loc.T("error.client", error));
        return fresh;
    }

    public async Task<IReadOnlyList<string>> ReadWebApiAsync(GameData game, CancellationToken ct = default)
    {
        var key = settings.RequireApiKey();
        var list = await api.GetPlayerAchievementsAsync(key, settings.SteamId64, game.AppId, ct);
        var fresh = GameLogic.ApplySteamState(game, list.Select(a => (a.ApiName, a.Achieved, a.UnlockTime)), DateTimeOffset.Now, lagging: true);
        game.PendingUnlocks.RemoveAll(n => game.StateOf(n).SteamUnlocked);
        games.Save(game);
        return fresh;
    }

    /// <summary>Status and schema through the helper. Error - why there is no answer.</summary>
    public async Task<(IReadOnlyList<string> Fresh, string? Error)> ReadViaClientAsync(GameData game, CancellationToken ct = default)
    {
        if (BridgeClient.SignedInElsewhere(settings.SteamId64) is { } other) return (Array.Empty<string>(), other);
        var r = await bridge.ReadAsync(game.AppId, settings.ContentLanguage, ct);
        if (!r.Ok || r.Achievements is null) return (Array.Empty<string>(), r.Error ?? Loc.T("error.bridge"));

        var byName = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        bool added = false;
        foreach (var a in r.Achievements)
        {
            if (!byName.TryGetValue(a.ApiName, out var def))
            {
                def = new AchievementDef { ApiName = a.ApiName };
                game.Achievements.Add(def);
                byName[a.ApiName] = def;
                added = true;
            }
            if (!string.IsNullOrEmpty(a.Name)) def.Name = a.Name;
            if (!string.IsNullOrEmpty(a.Description)) def.Description = a.Description;
            def.Hidden = a.Hidden;
            def.Protected = a.Protected;
            if (!string.IsNullOrEmpty(a.Icon)) def.IconUrl = a.Icon;
            if (!string.IsNullOrEmpty(a.IconGray)) def.IconGrayUrl = a.IconGray;
        }
        var fresh = GameLogic.ApplySteamState(game,
            r.Achievements.Select(a => (a.ApiName, a.Achieved, a.UnlockTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(a.UnlockTime) : (DateTimeOffset?)null)),
            DateTimeOffset.Now);
        game.PendingUnlocks.RemoveAll(n => game.StateOf(n).SteamUnlocked);
        // Only for achievements this read brought: the load asks for the others, and a poll during play would ask
        // every time for a game Steam has no percentages of.
        if (added) await percentages.FetchAsync(game, steam: true, ct);
        GameLogic.AutoClassify(game);
        TimeEstimator.EstimateFromGlobalPercent(game);
        games.Save(game);
        return (fresh, null);
    }

    /// <summary>
    /// Whether opening a game reads its status through the Steam client, which shows the user in that game to friends
    /// for a moment: only for a game never read, a running one, or one whose stats Steam has written since the read.
    /// </summary>
    public bool MayHaveChanged(GameData game, bool running) =>
        MayHaveChanged(game.StateFetchedAt, SteamLocal.StatsWrittenAt(settings.SteamId64, game.AppId), running);

    internal static bool MayHaveChanged(DateTimeOffset? readAt, DateTimeOffset? statsWrittenAt, bool running) =>
        readAt is null || running || statsWrittenAt > readAt + StatsWriteLag;

    // Steam may write the stats file a moment after a read through the client: that is not a change.
    private static readonly TimeSpan StatsWriteLag = TimeSpan.FromMinutes(1);
}
