using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

/// <param name="PercentagesMissing">Steam and Steam Hunters both answered and have no percentages for the game.</param>
public sealed record LoadReport(GameData Game, IReadOnlyList<string> NewlyUnlocked, IReadOnlyList<string> Warnings,
    bool PercentagesMissing = false);

public sealed record MarkReport(bool SteamWritten, string? Error);

/// <summary>
/// App use cases: load a game, refresh the status, mark, write marks to Steam.
/// Changes are saved to disk right away.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GameService
{
    private static readonly TimeSpan SchemaTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan CompletionTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan GuidesTtl = TimeSpan.FromDays(30);

    private readonly JsonStore _store;
    private readonly SteamWebApi _api;
    private readonly BridgeClient _bridge;
    private readonly SteamHunters _hunters;
    private readonly SteamGuides _guides;
    private readonly Translator _translator = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
    /// <summary>When reading a game's guides last failed: not tried again for a day.</summary>
    private readonly Dictionary<uint, DateTimeOffset> _guidesFailed = new();

    public AppSettings Settings { get; private set; }
    public UpdateService Updates { get; }
    public JsonStore Store => _store;
    public BridgeClient Bridge => _bridge;

    public GameService(JsonStore store, SteamWebApi api, BridgeClient bridge, SteamHunters hunters, UpdateService? updates = null)
    {
        _store = store;
        Updates = updates ?? new UpdateService(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        _api = api;
        _bridge = bridge;
        _hunters = hunters;
        _guides = new SteamGuides(api);
        Settings = store.LoadSettings();
        if (Settings.SteamId64 == 0 && SteamLocal.MostRecentUser() is { } u)
        {
            Settings.SteamId64 = u.SteamId;
            _store.SaveSettings(Settings);
        }
    }

    public static GameService CreateDefault()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return new GameService(new JsonStore(), new SteamWebApi(http), new BridgeClient(), new SteamHunters(http));
    }

    public string ApiKey
    {
        get => Dpapi.Unprotect(Settings.ApiKeyProtected);
        set => Settings.ApiKeyProtected = Dpapi.Protect(value.Trim());
    }

    public bool HasApiKey => !string.IsNullOrEmpty(ApiKey);

    public void SaveSettings() => _store.SaveSettings(Settings);

    /// <summary>Steam language of achievement names and descriptions: the chosen one or the interface language.</summary>
    public string ContentLanguage => Settings.Language.Length > 0 ? Settings.Language : Loc.SteamLanguage(Loc.Language);

    /// <summary>The cached names and descriptions are in another language than the one now chosen.</summary>
    public bool NeedsSchemaReload(GameData game) =>
        !string.Equals(game.SchemaLanguage, ContentLanguage, StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-derives automatic types and groups, so group names follow the interface language.</summary>
    public void Relabel(GameData game)
    {
        GameLogic.AutoClassify(game);
        _store.SaveGame(game);
    }

    // ---------- library ----------

    public List<OwnedGame> CachedLibrary() => _store.LoadLibrary() ?? new List<OwnedGame>();

    public async Task<List<OwnedGame>> RefreshLibraryAsync(CancellationToken ct = default)
    {
        RequireKey();
        var games = await _api.GetOwnedGamesAsync(ApiKey, Settings.SteamId64, ct);
        games = games.OrderByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue).ThenBy(g => g.Name).ToList();
        _store.SaveLibrary(games);
        return games;
    }

    // ---------- game ----------

    public GameData? Cached(uint appId) => _store.LoadGame(appId);

    /// <summary>
    /// Loads a game: schema (from the cache if fresh), global %, the local Steam schema,
    /// player status from the Web API. A failed step does not fail the load but goes to Warnings.
    /// </summary>
    /// <param name="running">The game is running: Steam's playtime shows only part of the session.</param>
    /// <param name="current">The open game on a reload: marks and notes made while Steam answers go to it and stay.</param>
    public async Task<LoadReport> LoadGameAsync(uint appId, string? knownName = null, bool forceSchema = false, bool running = false,
        GameData? current = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var game = current ?? _store.LoadGame(appId) ?? new GameData { AppId = appId, Name = knownName ?? "" };
        game.CountManualMarks = Settings.ManualUnlocks;
        if (!string.IsNullOrEmpty(knownName) && string.IsNullOrEmpty(game.Name)) game.Name = knownName;

        var lib = CachedLibrary().FirstOrDefault(g => g.AppId == appId);
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
            if (HasApiKey)
            {
                try
                {
                    var (name, entries) = await _api.GetSchemaAsync(ApiKey, appId, ContentLanguage, ct);
                    if (!string.IsNullOrEmpty(name)) game.Name = name;
                    MergeSchema(game, entries);
                    game.SchemaFetchedAt = DateTimeOffset.Now;
                    game.SchemaLanguage = ContentLanguage;
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
        game.StatProgress = SteamLocal.ReadProgress(appId, Settings.SteamId64);

        // Percentages go after the list is built from all sources, otherwise some are lost without a key.
        bool percentagesMissing = false;
        if (stale || game.Achievements.Any(a => a.GlobalPercent is null))
        {
            var (error, missing) = await FetchPercentagesAsync(game, steam: fatal is null, ct);
            percentagesMissing = missing;
            if (error != null) Warn("warn.percents", error);
            else if (missing) warnings.Add(Loc.T("warn.percents", Loc.T("warn.noPercents")));
        }

        await FetchCompletionIfDueAsync(game, ct);

        IReadOnlyList<string> fresh = Array.Empty<string>();
        if (fatal is null && Settings.StatusSource == StatusSource.WebApi && HasApiKey && Settings.SteamId64 != 0)
        {
            try
            {
                fresh = await RefreshStateAsync(game, ct);
            }
            catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
            {
                Warn("warn.status", e);
            }
        }

        if (fatal is null && HasApiKey && Settings.SteamId64 != 0)
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
        _store.SaveGame(game);
        foreach (var w in warnings) AppLog.Info($"{appId}: {w}");
        return new LoadReport(game, fresh, warnings, percentagesMissing);
    }

    /// <summary>Web API status. Returns the newly unlocked ones.</summary>
    public async Task<IReadOnlyList<string>> RefreshStateAsync(GameData game, CancellationToken ct = default)
    {
        RequireKey();
        var list = await _api.GetPlayerAchievementsAsync(ApiKey, Settings.SteamId64, game.AppId, ct);
        var fresh = GameLogic.ApplySteamState(game, list.Select(a => (a.ApiName, a.Achieved, a.UnlockTime)), DateTimeOffset.Now);
        game.PendingUnlocks.RemoveAll(n => game.StateOf(n).SteamUnlocked);
        _store.SaveGame(game);
        return fresh;
    }

    /// <summary>
    /// Status auto refresh from the chosen source. With no Web API key - always via the Steam client.
    /// Returns the newly unlocked ones (already "unlocked"; manual marks resolve themselves).
    /// </summary>
    public async Task<IReadOnlyList<string>> RefreshStateAutoAsync(GameData game, CancellationToken ct = default)
    {
        game.StatProgress = SteamLocal.ReadProgress(game.AppId, Settings.SteamId64);
        bool useClient = Settings.StatusSource == StatusSource.SteamClient || !HasApiKey || Settings.SteamId64 == 0;
        if (!useClient) return await RefreshStateAsync(game, ct);
        var (fresh, error) = await RefreshViaBridgeAsync(game, ct);
        if (error != null) throw new SteamApiException(Loc.T("error.client", error));
        return fresh;
    }

    /// <summary>
    /// Fresh Steam playtime of the game. Between sessions it also goes to the playtime history, together with the
    /// playtime two weeks back (from Steam's "last two weeks"): the pace then skips stretches without progress.
    /// A session the app saw from its start adds its start too: the play since the last unlock is then known exactly.
    /// </summary>
    /// <param name="running">The game is running: mid-session Steam has not counted all of it yet.</param>
    public async Task RefreshPlaytimeAsync(GameData game, PlaySession? session = null, bool running = false, CancellationToken ct = default)
    {
        RequireKey();
        var own = await _api.GetOwnedGameAsync(ApiKey, Settings.SteamId64, game.AppId, ct);
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
        _store.SaveGame(game);
    }

    /// <summary>
    /// Tips from Steam Community guides: in the content language, English ones where those have no words of their own.
    /// Kept for a month and read again when the content language changes. Null - no key and nothing kept.
    /// </summary>
    public async Task<GuideTips?> GetGuideTipsAsync(GameData game, CancellationToken ct = default)
    {
        var kept = _store.LoadGuides(game.AppId);
        bool fresh = kept is not null && kept.Format == GuideTips.CurrentFormat && kept.Language == ContentLanguage &&
                     DateTimeOffset.Now - kept.FetchedAt < GuidesTtl;
        bool failed = _guidesFailed.TryGetValue(game.AppId, out var at) && DateTimeOffset.Now - at < TimeSpan.FromDays(1);
        if (fresh || failed || !HasApiKey || game.Achievements.Count == 0) return kept;
        try
        {
            var key = ApiKey;
            bool english = string.Equals(ContentLanguage, "english", StringComparison.OrdinalIgnoreCase);
            var names = english ? null : await EnglishNamesAsync(game, key, ct);
            var englishName = (names ?? []).DistinctBy(n => n.ApiName).ToDictionary(n => n.ApiName, n => n.Name, StringComparer.Ordinal);
            var own = game.Achievements
                .Select(a => new GuideAchievement(a.ApiName, a.Name, a.Description, englishName.GetValueOrDefault(a.ApiName, "")))
                .ToList();
            var mine = english ? new Dictionary<string, GuideTip>() : await _guides.TipsAsync(key, game.AppId, game.Name, ContentLanguage, own, ct);
            var theirs = (english ? own : names) is { } list
                ? await _guides.TipsAsync(key, game.AppId, game.Name, "english", list, ct)
                : new Dictionary<string, GuideTip>();
            var tips = new GuideTips
            {
                Format = GuideTips.CurrentFormat, Language = ContentLanguage, FetchedAt = DateTimeOffset.Now, Tips = GuideTips.Combine(mine, theirs),
            };
            _store.SaveGuides(game.AppId, tips);
            return tips;
        }
        catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
        {
            _guidesFailed[game.AppId] = DateTimeOffset.Now;
            AppLog.Info($"{game.AppId}: guides: {e.Message}");
            return kept;
        }
    }

    /// <summary>
    /// A tip in another language (ISO code), translated once and kept with the tips.
    /// Null - no words to translate, or they already are in that language.
    /// </summary>
    public async Task<string?> TranslateTipAsync(uint appId, GuideTips tips, string apiName, string language, CancellationToken ct = default)
    {
        var key = language + ":" + apiName;
        if (tips.Translations.TryGetValue(key, out var done)) return done;
        if (!tips.Tips.TryGetValue(apiName, out var tip) || tip.Text.Length == 0) return null;
        var text = await _translator.TranslateAsync(tip.Text, language, ct);
        if (text is null) return null;
        tips.Translations[key] = text;
        _store.SaveGuides(appId, tips);
        return text;
    }

    /// <summary>English names to find achievements in English guides: from the local schema, else from the Web API.</summary>
    private async Task<List<GuideAchievement>?> EnglishNamesAsync(GameData game, string key, CancellationToken ct)
    {
        if (SteamLocal.ReadSchema(game.AppId, "english") is { Count: > 0 } local)
            return local.Select(a => new GuideAchievement(a.ApiName, a.Name, a.Description)).ToList();
        var (_, entries) = await _api.GetSchemaAsync(key, game.AppId, "english", ct);
        return entries.Count == 0 ? null : entries.Select(e => new GuideAchievement(e.ApiName, e.Name, e.Description)).ToList();
    }

    /// <summary>Own "actually played" time (null - from Steam) and Steam hours to ignore.</summary>
    public void SetPlaytime(GameData game, double? overrideMinutes, double offsetMinutes)
    {
        overrideMinutes = overrideMinutes is >= 0 ? overrideMinutes : null;
        if (overrideMinutes != game.PlaytimeOverrideMinutes) game.PlaytimeOverrideAt = overrideMinutes is null ? null : DateTimeOffset.Now;
        game.PlaytimeOverrideMinutes = overrideMinutes;
        game.PlaytimeOffsetMinutes = Math.Max(0, offsetMinutes);
        _store.SaveGame(game);
    }

    /// <summary>Status and schema via the helper (works with private profiles, shows "in game" for a second).</summary>
    public async Task<(IReadOnlyList<string> Fresh, string? Error)> RefreshViaBridgeAsync(GameData game, CancellationToken ct = default)
    {
        if (SignedInElsewhere() is { } other) return (Array.Empty<string>(), other);
        var r = await _bridge.ReadAsync(game.AppId, ContentLanguage, ct);
        if (!r.Ok || r.Achievements is null) return (Array.Empty<string>(), r.Error ?? Loc.T("error.bridge"));

        var byName = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        foreach (var a in r.Achievements)
        {
            if (!byName.TryGetValue(a.ApiName, out var def))
            {
                def = new AchievementDef { ApiName = a.ApiName };
                game.Achievements.Add(def);
                byName[a.ApiName] = def;
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
        if (game.Achievements.Any(a => a.GlobalPercent is null)) await FetchPercentagesAsync(game, steam: true, ct);
        GameLogic.AutoClassify(game);
        TimeEstimator.EstimateFromGlobalPercent(game);
        _store.SaveGame(game);
        return (fresh, null);
    }

    /// <summary>
    /// Own stats from a player sample: reads achievements of the game's review authors
    /// (public profiles) and computes percentages and time to each achievement.
    /// Needs an API key; ~100 requests, usually 20-60 seconds.
    /// </summary>
    public async Task<SampleResult> CollectSampleAsync(GameData game, IProgress<string>? progress = null,
        int maxAuthors = 300, int targetPlayers = 80, CancellationToken ct = default)
    {
        RequireKey();
        var key = ApiKey;
        progress?.Report(Loc.T("sample.searching"));
        var authors = await _api.GetReviewAuthorsAsync(game.AppId, maxAuthors, ct);
        authors = authors.Where(a => a.SteamId != Settings.SteamId64).ToList();
        if (authors.Count == 0) throw new SteamApiException(Loc.T("sample.noReviews"));

        var samples = new List<PlayerSample>();
        SteamApiException? fatal = null;
        int checkedCount = 0;
        using var gate = new SemaphoreSlim(4);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = authors.Select(async a =>
        {
            try { await gate.WaitAsync(stop.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (stop.IsCancellationRequested) return;
                var list = await _api.GetPlayerAchievementsAsync(key, a.SteamId, game.AppId, stop.Token);
                var unlocks = list.Where(x => x.Achieved && x.UnlockTime is not null)
                                  .ToDictionary(x => x.ApiName, x => x.UnlockTime!.Value, StringComparer.Ordinal);
                var sample = new PlayerSample { SteamId = a.SteamId, Unlocks = unlocks };
                if (a.ReviewedAt is { } rv && a.PlaytimeAtReview > 0) sample.Anchors.Add((rv, a.PlaytimeAtReview));
                if (a.LastPlayed is { } lp && a.PlaytimeForever > 0) sample.Anchors.Add((lp, a.PlaytimeForever));
                bool enough;
                lock (samples)
                {
                    samples.Add(sample);
                    enough = samples.Count >= targetPlayers;
                }
                if (enough) stop.Cancel();
            }
            catch (SteamApiException e) when (e.Fatal)
            {
                // A blocked network or a rejected key fails every player the same way.
                fatal ??= e;
                stop.Cancel();
            }
            catch (Exception e) when (e is SteamApiException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                // private profile or failure - skip the player
            }
            finally
            {
                var c = Interlocked.Increment(ref checkedCount);
                if (c % 10 == 0) progress?.Report(Loc.T("sample.progress", c, authors.Count, samples.Count));
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        if (fatal != null) throw fatal;
        ct.ThrowIfCancellationRequested();

        if (samples.Count == 0)
            throw new SteamApiException(Loc.Plural("sample.allPrivate", authors.Count));

        // Only compute: the caller applies it to the current game object (it may have been reloaded meanwhile).
        return SampleStats.Compute(samples, game.Achievements.Select(a => a.ApiName).ToList());
    }

    public void ApplySample(GameData game, SampleResult r)
    {
        SampleStats.Apply(game, r, DateTimeOffset.Now);
        TimeEstimator.EstimateFromGlobalPercent(game);
        _store.SaveGame(game);
    }

    /// <summary>Global percentages (no key needed). Returns the error text or null.</summary>
    /// <summary>
    /// Global percentages from Steam, else from Steam Hunters (it also answers when Steam's API is blocked).
    /// Error - Steam's failure (always when the network or key is turned away, otherwise only when nothing came);
    /// Missing - both answered and have none.
    /// </summary>
    private async Task<(Exception? Error, bool Missing)> FetchPercentagesAsync(GameData game, bool steam, CancellationToken ct)
    {
        if (game.Achievements.Count == 0) return (null, false);
        Exception? steamError = null, huntersError = null;
        var pct = new Dictionary<string, double>();
        if (steam)
        {
            try
            {
                pct = await _api.GetGlobalPercentagesAsync(game.AppId, ct);
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
                pct = await _hunters.GetGlobalPercentagesAsync(game.AppId, ct);
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

    /// <summary>Time to 100% from Steam Hunters, once a week, unless it was set by hand.</summary>
    private async Task FetchCompletionIfDueAsync(GameData game, CancellationToken ct)
    {
        if (game.CompletionSource == "manual" ||
            game.CompletionFetchedAt is { } at && DateTimeOffset.Now - at < CompletionTtl) return;
        try
        {
            var minutes = await _hunters.GetMedianCompletionMinutesAsync(game.AppId, ct);
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
    /// Writes to Steam right now, regardless of the mode in the settings.
    /// Also sets the manual "done" mark. Protected and unlocked ones are skipped.
    /// </summary>
    public async Task<MarkReport> UnlockInSteamAsync(GameData game, IReadOnlyList<string> apiNames, CancellationToken ct = default)
    {
        if (ManualUnlocksOff() is { } off) return off;
        var byName = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        var skipped = new List<string>();
        var toWrite = new List<string>();
        foreach (var n in apiNames.Distinct(StringComparer.Ordinal))
        {
            if (game.StateOf(n).SteamUnlocked) continue;
            if (byName.TryGetValue(n, out var d) && d.Protected) { skipped.Add(d.Name.Length > 0 ? d.Name : n); continue; }
            var s = game.StateOf(n);
            if (!s.ManualDone) { s.ManualDone = true; s.ManualAt = DateTimeOffset.Now; }
            toWrite.Add(n);
        }
        _store.SaveGame(game);
        if (toWrite.Count == 0)
            return new MarkReport(false, skipped.Count > 0 ? Loc.T("error.protected", string.Join(", ", skipped)) : Loc.T("error.nothingToWrite"));

        var r = await WriteToSteamAsync(game, toWrite, unlock: true, ct);
        _store.SaveGame(game);
        if (skipped.Count > 0)
            r = r with { Error = (r.Error is null ? "" : r.Error + "; ") + Loc.T("error.skippedProtected", string.Join(", ", skipped)) };
        return r;
    }

    // ---------- marks ----------

    /// <summary>
    /// Manual "done but not counted" mark. What happens in Steam depends on Settings.WriteMode.
    /// Removing the mark changes nothing in Steam (RevertInSteamAsync does that).
    /// </summary>
    public async Task<MarkReport> SetManualDoneAsync(GameData game, string apiName, bool done, CancellationToken ct = default)
    {
        if (ManualUnlocksOff() is { } off) return off;
        var s = game.StateOf(apiName);
        s.ManualDone = done;
        s.ManualAt = done ? DateTimeOffset.Now : null;
        if (!done) game.PendingUnlocks.Remove(apiName);

        // Save the mark before calling Steam so it is not lost if the helper crashes.
        _store.SaveGame(game);

        MarkReport report = new(false, null);
        var def = game.Achievements.FirstOrDefault(a => a.ApiName == apiName);
        if (done && !s.SteamUnlocked && def is { Protected: false })
        {
            switch (Settings.WriteMode)
            {
                case WriteMode.MarkAndUnlock:
                    report = await WriteToSteamAsync(game, new[] { apiName }, unlock: true, ct);
                    break;
                case WriteMode.Queue:
                    if (!game.PendingUnlocks.Contains(apiName)) game.PendingUnlocks.Add(apiName);
                    break;
            }
        }
        else if (done && def is { Protected: true } && Settings.WriteMode != WriteMode.MarkOnly)
        {
            report = new MarkReport(false, Loc.T("error.protectedOne"));
        }
        _store.SaveGame(game);
        return report;
    }

    public void SetNote(GameData game, string apiName, string note)
    {
        game.StateOf(apiName).Note = note;
        _store.SaveGame(game);
    }

    public void SetOffOverlay(GameData game, string apiName, bool off)
    {
        game.StateOf(apiName).OffOverlay = off;
        _store.SaveGame(game);
    }

    public void SetKind(GameData game, string apiName, AchievementKind kind)
    {
        GameLogic.SetKind(game, apiName, kind);
        _store.SaveGame(game);
    }

    public void SetGroup(GameData game, string apiName, string group)
    {
        GameLogic.SetGroup(game, apiName, group);
        _store.SaveGame(game);
    }

    public void SetManualEstimate(GameData game, string apiName, double? minutes)
    {
        if (minutes is > 0) game.ManualEstimates[apiName] = minutes.Value;
        else game.ManualEstimates.Remove(apiName);
        _store.SaveGame(game);
    }

    public void SetCompletionMinutes(GameData game, double? minutes, string source)
    {
        game.CompletionMinutes = minutes is > 0 ? minutes : null;
        game.CompletionSource = game.CompletionMinutes is null ? "" : source;
        TimeEstimator.EstimateFromGlobalPercent(game);
        _store.SaveGame(game);
    }

    public async Task<double?> FetchCompletionAsync(GameData game, CancellationToken ct = default)
    {
        var m = await _hunters.GetMedianCompletionMinutesAsync(game.AppId, ct);
        game.CompletionFetchedAt = DateTimeOffset.Now;
        if (m is > 0) SetCompletionMinutes(game, m, SteamHunters.Name);
        return m;
    }

    /// <summary>Sends the queued marks to Steam.</summary>
    public async Task<MarkReport> FlushQueueAsync(GameData game, CancellationToken ct = default)
    {
        if (ManualUnlocksOff() is { } off) return off;
        var names = game.PendingUnlocks.Where(n => !game.StateOf(n).SteamUnlocked).ToList();
        if (names.Count == 0) return new MarkReport(false, null);
        var r = await WriteToSteamAsync(game, names, unlock: true, ct);
        _store.SaveGame(game);
        return r;
    }

    /// <summary>Locks in Steam an achievement the app unlocked (according to the log).</summary>
    public async Task<MarkReport> RevertInSteamAsync(GameData game, string apiName, CancellationToken ct = default)
    {
        if (ManualUnlocksOff() is { } off) return off;
        var r = await WriteToSteamAsync(game, new[] { apiName }, unlock: false, ct);
        _store.SaveGame(game);
        return r;
    }

    /// <summary>Whether the app unlocked the achievement in Steam and did not relock it.</summary>
    public bool WasWrittenByApp(uint appId, string apiName)
    {
        var last = _store.ReadLog(appId).LastOrDefault(e => e.ApiName == apiName && e.Ok);
        return last?.Action == "unlock";
    }

    /// <summary>
    /// Whether opening a game reads its status through the Steam client, which shows the user in that game to friends
    /// for a moment: only for a game never read, a running one, or one whose stats Steam has written since the read.
    /// </summary>
    public bool StatusMayHaveChanged(GameData game, bool running) =>
        StatusMayHaveChanged(game.StateFetchedAt, SteamLocal.StatsWrittenAt(Settings.SteamId64, game.AppId), running);

    internal static bool StatusMayHaveChanged(DateTimeOffset? readAt, DateTimeOffset? statsWrittenAt, bool running) =>
        readAt is null || running || statsWrittenAt > readAt + StatsWriteLag;

    // Steam may write the stats file a moment after a read through the client: that is not a change.
    private static readonly TimeSpan StatsWriteLag = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The helper reads and writes as whoever is signed in to the Steam client: another account than the one in the
    /// settings would get the marks, and its status would mix into this one's. null - the same, or nothing to compare.
    /// </summary>
    private string? SignedInElsewhere() =>
        OtherAccount(SteamLocal.SignedInAccountId(), Settings.SteamId64) ? Loc.T("error.otherAccount") : null;

    /// <summary>The Steam client is signed in to an account, and not to this one; false - the same, or unknown.</summary>
    internal static bool OtherAccount(uint signedIn, ulong steamId64) =>
        signedIn != 0 && steamId64 != 0 && signedIn != SteamLocal.AccountId(steamId64);

    private async Task<MarkReport> WriteToSteamAsync(GameData game, IReadOnlyList<string> names, bool unlock, CancellationToken ct)
    {
        if (SignedInElsewhere() is { } other) return new MarkReport(false, other);
        var r = unlock ? await _bridge.UnlockAsync(game.AppId, names, ct) : await _bridge.LockAsync(game.AppId, names, ct);
        var now = DateTimeOffset.Now;
        var action = unlock ? "unlock" : "lock";

        if (r.Changes is null)
        {
            foreach (var n in names)
                _store.AppendLog(new UnlockLogEntry { At = now, AppId = game.AppId, ApiName = n, Action = action, Ok = false, Error = r.Error });
            return new MarkReport(false, r.Error ?? Loc.T("error.bridgeNoResult"));
        }

        foreach (var c in r.Changes)
        {
            _store.AppendLog(new UnlockLogEntry { At = now, AppId = game.AppId, ApiName = c.ApiName, Action = action, Ok = c.Ok, Error = c.Error });
            if (!c.Ok) continue;
            var s = game.StateOf(c.ApiName);
            s.SteamUnlocked = unlock;
            s.SteamUnlockTime = unlock ? now : null;
            game.PendingUnlocks.Remove(c.ApiName);
        }
        var failed = r.Changes.Where(c => !c.Ok).ToList();
        return new MarkReport(r.Changes.Any(c => c.Ok),
            failed.Count == 0 ? null : string.Join("; ", failed.Select(f => $"{f.ApiName}: {f.Error}")));
    }

    // ---------- import/export ----------

    public int ImportTags(GameData game, string json)
    {
        var n = TagPack.FromJson(json).ApplyTo(game);
        TimeEstimator.EstimateFromGlobalPercent(game);
        _store.SaveGame(game);
        return n;
    }

    // ---------- internals ----------

    private MarkReport? ManualUnlocksOff() =>
        Settings.ManualUnlocks ? null : new MarkReport(false, Loc.T("error.manualOff"));

    private void RequireKey()
    {
        if (!HasApiKey) throw new SteamApiException(Loc.T("error.noKey"));
        if (Settings.SteamId64 == 0) throw new SteamApiException(Loc.T("error.noSteamId"));
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
        var local = SteamLocal.ReadSchema(game.AppId, ContentLanguage);
        if (local is null || local.Count == 0) return;
        // Without a key the local schema is the only source of texts, so it also decides their language.
        bool textsFromLocal = !HasApiKey;
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
            if ((textsFromLocal && l.Description.Length > 0) || string.IsNullOrEmpty(def.Description)) def.Description = l.Description;
            def.Protected = l.Protected;
        }
        if (textsFromLocal) game.SchemaLanguage = ContentLanguage;
    }
}
