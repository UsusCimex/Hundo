using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

/// <summary>Global percentages of a game's achievements from Steam, else from Steam Hunters. No key needed.</summary>
[SupportedOSPlatform("windows")]
public sealed class GlobalPercentages(SteamWebApi api, SteamHunters hunters)
{
    /// <summary>
    /// Writes the percentages into the game. Steam Hunters also answers when Steam's API is blocked.
    /// Error - Steam's failure (always when the network or key is turned away, otherwise only when nothing came);
    /// Missing - both answered and have none.
    /// </summary>
    /// <param name="steam">Steam's API may be asked: it has not turned the network away during this load.</param>
    public async Task<(Exception? Error, bool Missing)> FetchAsync(GameData game, bool steam, CancellationToken ct)
    {
        if (game.Achievements.Count == 0) return (null, false);
        Exception? steamError = null, huntersError = null;
        var pct = new Dictionary<string, double>();
        if (steam)
        {
            try
            {
                pct = await api.GetGlobalPercentagesAsync(game.AppId, ct);
            }
            catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
            {
                steamError = e;
            }
        }
        if (pct.Count == 0)
        {
            try
            {
                pct = await hunters.GetGlobalPercentagesAsync(game.AppId, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                huntersError = e;
                AppLog.Info($"{game.AppId}: {SteamHunters.Name}: {e.Message}");
            }
        }
        foreach (var a in game.Achievements)
            if (pct.TryGetValue(a.ApiName, out var v)) a.GlobalPercent = v;
        if (pct.Count > 0) return (steamError is SteamApiException { Fatal: true } ? steamError : null, false);
        return (steamError ?? huntersError, steam && steamError is null && huntersError is null);
    }
}
