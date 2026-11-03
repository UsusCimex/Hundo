using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

/// <param name="PercentagesMissing">Steam and Steam Hunters both answered and have no percentages for the game.</param>
public sealed record LoadReport(GameData Game, IReadOnlyList<string> NewlyUnlocked, IReadOnlyList<string> Warnings,
    bool PercentagesMissing = false);

/// <summary>
/// Fills a game from Steam and the PC: the schema, the local schema with hidden descriptions, global percentages, the
/// time to 100%, the status and the playtime.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameLoader(SteamWebApi api, SteamHunters hunters, Games games, StatusReader status,
    GlobalPercentages percentages, SteamLibrary library, AppSettings settings)
{
    private static readonly TimeSpan SchemaTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan CompletionTtl = TimeSpan.FromDays(7);

    /// <summary>The cached names and descriptions are in another language than the one now chosen.</summary>
    public bool NeedsSchemaReload(GameData game) =>
        !string.Equals(game.SchemaLanguage, settings.ContentLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads a game: schema (from the cache if fresh), global %, the local Steam schema,
    /// player status from the Web API. A failed step does not fail the load but goes to Warnings.
    /// </summary>
    /// <param name="running">The game is running: Steam's playtime shows only part of the session.</param>
    /// <param name="current">The open game on a reload: marks and notes made while Steam answers go to it and stay.</param>
    public async Task<LoadReport> LoadAsync(uint appId, string? knownName = null, bool forceSchema = false, bool running = false,
        GameData? current = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var game = current is null ? games.Open(appId, knownName) : games.WithMarkSettings(current);
        if (!string.IsNullOrEmpty(knownName) && string.IsNullOrEmpty(game.Name)) game.Name = knownName;

        var lib = library.Cached().FirstOrDefault(g => g.AppId == appId);
        // Steam playtime only grows: an old library cache must not undo a fresher value saved with the game.
        if (lib != null && lib.PlaytimeMinutes > game.PlaytimeMinutes)
        {
            game.PlaytimeMinutes = lib.PlaytimeMinutes;
            game.PlaytimeReadAt = null;
        }

        bool stale = forceSchema || game.Achievements.Count == 0 || game.SchemaFetchedAt is null ||
                     DateTimeOffset.Now - game.SchemaFetchedAt > SchemaTtl ||
                     NeedsSchemaReload(game);

        // Once Steam's API has turned the network or the key away, its other requests would fail the same way:
        // one warning, and the remaining API steps are skipped.
        SteamApiException? fatal = null;
        void Warn(string key, Exception e)
        {
            if (e is SteamApiException { Fatal: true } f)
            {
                if (fatal is null) warnings.Add(f.Message);
                fatal ??= f;
            }
            else warnings.Add(Loc.T(key, e.Message));
        }

        if (stale)
        {
            if (settings.HasApiKey)
            {
                try
                {
                    var language = settings.ContentLanguage;
                    var (name, entries) = await api.GetSchemaAsync(settings.ApiKey, appId, language, ct);
                    if (!string.IsNullOrEmpty(name)) game.Name = name;
                    MergeSchema(game, entries);
                    game.SchemaFetchedAt = DateTimeOffset.Now;
                    game.SchemaLanguage = language;
                }
                catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
                {
                    Warn("warn.schema", e);
                }
            }
            else warnings.Add(Loc.T("warn.noKeySchema"));
        }

        // Local schema: hidden descriptions and the "protected" flag; cheap, always read.
        MergeLocalSchema(game);
        game.StatProgress = SteamLocal.ReadProgress(appId, settings.SteamId64);

        // Percentages go after the list is built from all sources, otherwise some are lost without a key.
        bool percentagesMissing = false;
        if (stale || game.Achievements.Any(a => a.GlobalPercent is null))
        {
            var (error, missing) = await percentages.FetchAsync(game, steam: fatal is null, ct);
            percentagesMissing = missing;
            if (error != null) Warn("warn.percents", error);
            else if (missing) warnings.Add(Loc.T("warn.percents", Loc.T("warn.noPercents")));
        }

        await FetchCompletionIfDueAsync(game, ct);

        IReadOnlyList<string> fresh = Array.Empty<string>();
        if (fatal is null && settings.StatusSource == StatusSource.WebApi && settings.HasApiKey && settings.SteamId64 != 0)
        {
            try
            {
                fresh = await status.ReadWebApiAsync(game, ct);
            }
            catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
            {
                Warn("warn.status", e);
            }
        }

        if (fatal is null && settings.HasApiKey && settings.SteamId64 != 0)
        {
            try
            {
                await RefreshPlaytimeAsync(game, running: running, ct: ct);
            }
            catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
            {
                AppLog.Info($"{appId}: playtime: {e.Message}");
            }
        }

        GameLogic.AutoClassify(game);
        TimeEstimator.EstimateFromGlobalPercent(game);
        games.Save(game);
        foreach (var w in warnings) AppLog.Info($"{appId}: {w}");
        return new LoadReport(game, fresh, warnings, percentagesMissing);
    }

    /// <summary>
    /// Fresh Steam playtime of the game. Between sessions it also goes to the playtime history, together with the
    /// playtime two weeks back (from Steam's "last two weeks"): the pace then skips stretches without progress.
    /// A session the app saw from its start adds its start too: the play since the last unlock is then known exactly.
    /// </summary>
    /// <param name="running">The game is running: mid-session Steam has not counted all of it yet.</param>
    public async Task RefreshPlaytimeAsync(GameData game, PlaySession? session = null, bool running = false, CancellationToken ct = default)
    {
        var key = settings.RequireApiKey();
        var own = await api.GetOwnedGameAsync(key, settings.SteamId64, game.AppId, ct);
        if (own is null) return;
        var now = DateTimeOffset.Now;
        game.PlaytimeMinutes = own.PlaytimeMinutes;
        game.PlaytimeReadAt = now;
        if (!running)
        {
            Progress.AddPlaytime(game, now.AddDays(-14), own.PlaytimeMinutes - own.Playtime2WeeksMinutes);
            if (session is not null && session.AppId == game.AppId) Progress.AddSessionStart(game, session, own.PlaytimeMinutes);
            Progress.AddPlaytime(game, now, own.PlaytimeMinutes);
        }
        games.Save(game);
    }

    /// <summary>The time to 100% from Steam Hunters now, asked by the user. Null - it has none.</summary>
    public async Task<double?> FetchCompletionAsync(GameData game, CancellationToken ct = default)
    {
        var m = await hunters.GetMedianCompletionMinutesAsync(game.AppId, ct);
        game.CompletionFetchedAt = DateTimeOffset.Now;
        if (m is > 0) games.SetCompletionMinutes(game, m, SteamHunters.Name);
        return m;
    }

    /// <summary>Time to 100% from Steam Hunters, once a week, unless it was set by hand.</summary>
    private async Task FetchCompletionIfDueAsync(GameData game, CancellationToken ct)
    {
        if (game.CompletionSource == "manual" ||
            game.CompletionFetchedAt is { } at && DateTimeOffset.Now - at < CompletionTtl) return;
        try
        {
            var minutes = await hunters.GetMedianCompletionMinutesAsync(game.AppId, ct);
            game.CompletionFetchedAt = DateTimeOffset.Now;
            if (minutes is > 0)
            {
                game.CompletionMinutes = minutes;
                game.CompletionSource = SteamHunters.Name;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppLog.Info($"{game.AppId}: {SteamHunters.Name}: {e.Message}");
        }
    }

    /// <summary>
    /// New Web API schema over the old: what the Web API omits (hidden descriptions, percentages,
    /// sample percentages, the "protected" flag) is carried over.
    /// </summary>
    internal static void MergeSchema(GameData game, IReadOnlyList<SchemaEntry> entries)
    {
        var old = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        var list = new List<AchievementDef>(entries.Count);
        foreach (var e in entries)
        {
            old.TryGetValue(e.ApiName, out var prev);
            list.Add(new AchievementDef
            {
                ApiName = e.ApiName,
                Name = e.Name,
                // the Web API omits hidden descriptions - keep what was known (from the local schema)
                Description = string.IsNullOrEmpty(e.Description) ? prev?.Description ?? "" : e.Description,
                Hidden = e.Hidden,
                IconUrl = e.Icon,
                IconGrayUrl = e.IconGray,
                GlobalPercent = prev?.GlobalPercent,
                SamplePercent = prev?.SamplePercent,
                Protected = prev?.Protected ?? false,
            });
        }
        game.Achievements = list;
    }

    private void MergeLocalSchema(GameData game)
    {
        var language = settings.ContentLanguage;
        var local = SteamLocal.ReadSchema(game.AppId, language);
        if (local is null || local.Count == 0) return;
        // Without a key the local schema is the only source of texts, so it also decides their language.
        bool textsFromLocal = !settings.HasApiKey;
        MergeLocalSchema(game, local, textsFromLocal);
        if (textsFromLocal) game.SchemaLanguage = language;
    }

    /// <summary>
    /// The local schema, in the content language, over the list: it adds what the Web API lacks, and gives the
    /// descriptions of hidden achievements, which the Web API never does.
    /// </summary>
    internal static void MergeLocalSchema(GameData game, IReadOnlyList<AC.Core.SteamFormats.SchemaAchievement> local, bool textsFromLocal)
    {
        var byName = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        foreach (var l in local)
        {
            if (!byName.TryGetValue(l.ApiName, out var def))
            {
                // no key or Web API is down - build the list from the local schema
                def = new AchievementDef
                {
                    ApiName = l.ApiName,
                    Name = l.Name,
                    Hidden = l.Hidden,
                    IconUrl = AC.Core.SteamFormats.StatsSchemaReader.IconUrl(game.AppId, l.Icon),
                    IconGrayUrl = AC.Core.SteamFormats.StatsSchemaReader.IconUrl(game.AppId, l.IconGray),
                };
                game.Achievements.Add(def);
                byName[l.ApiName] = def;
            }
            if (textsFromLocal && l.Name.Length > 0) def.Name = l.Name;
            if (l.Description.Length > 0 && (textsFromLocal || def.Hidden) || string.IsNullOrEmpty(def.Description)) def.Description = l.Description;
            def.Protected = l.Protected;
        }
    }
}
