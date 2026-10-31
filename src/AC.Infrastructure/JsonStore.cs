using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AC.Core;

namespace AC.Infrastructure;

/// <summary>
/// Local storage: a JSON file per game, settings, the library cache, the progress summary and the Steam write log.
/// Default folder is AppPaths.DataRoot (usually %LocalAppData%\AchievementCompanion).
/// </summary>
public sealed class JsonStore
{
    private readonly object _logLock = new();
    private readonly object _summaryLock = new();
    private Dictionary<uint, GameSummary>? _summaries;

    public string Root { get; }
    private string GamesDir => Path.Combine(Root, "games");
    private string SettingsPath => Path.Combine(Root, "settings.json");
    private string LibraryPath => Path.Combine(Root, "library.json");
    private string SummaryPath => Path.Combine(Root, "summary.json");
    private string GuidesDir => Path.Combine(Root, "cache", "guides");
    public string LogPath => Path.Combine(Root, "unlock_log.jsonl");

    public JsonStore(string? root = null)
    {
        Root = root ?? AppPaths.DataRoot;
        Directory.CreateDirectory(GamesDir);
    }

    public string GamePath(uint appId) => Path.Combine(GamesDir, appId.ToString(CultureInfo.InvariantCulture) + ".json");

    public GameData? LoadGame(uint appId) => Read<GameData>(GamePath(appId));

    public void SaveGame(GameData game)
    {
        Write(GamePath(game.AppId), game);
        UpdateSummary(game);
    }

    public GuideTips? LoadGuides(uint appId) => Read<GuideTips>(GuidesPath(appId));

    public void SaveGuides(uint appId, GuideTips tips)
    {
        Directory.CreateDirectory(GuidesDir);
        Write(GuidesPath(appId), tips);
    }

    private string GuidesPath(uint appId) => Path.Combine(GuidesDir, appId.ToString(CultureInfo.InvariantCulture) + ".json");

    public AppSettings LoadSettings() => Read<AppSettings>(SettingsPath) ?? new AppSettings();

    public void SaveSettings(AppSettings s) => Write(SettingsPath, s);

    public List<OwnedGame>? LoadLibrary() => Read<List<OwnedGame>>(LibraryPath);

    public void SaveLibrary(List<OwnedGame> games) => Write(LibraryPath, games);

    /// <summary>Progress of every game opened in the app, so the library does not read the game files.</summary>
    public IReadOnlyDictionary<uint, GameSummary> LoadSummaries()
    {
        lock (_summaryLock) return new Dictionary<uint, GameSummary>(Summaries());
    }

    private Dictionary<uint, GameSummary> Summaries()
    {
        if (_summaries != null) return _summaries;
        _summaries = Read<Dictionary<uint, GameSummary>>(SummaryPath);
        if (_summaries is null || _summaries.Values.Any(s => s.Format < GameSummary.CurrentFormat))
        {
            // No summary yet, or one without the newer fields: build it once from the games already saved.
            _summaries = new();
            foreach (var file in Directory.EnumerateFiles(GamesDir, "*.json"))
            {
                if (!uint.TryParse(Path.GetFileNameWithoutExtension(file), out var appId)) continue;
                if (Read<GameData>(file) is { Achievements.Count: > 0 } g) _summaries[appId] = GameSummary.Of(g);
            }
            Write(SummaryPath, _summaries);
        }
        return _summaries;
    }

    private void UpdateSummary(GameData game)
    {
        if (game.Achievements.Count == 0) return;
        var fresh = GameSummary.Of(game);
        lock (_summaryLock)
        {
            var all = Summaries();
            if (all.TryGetValue(game.AppId, out var old) && old.Equals(fresh)) return;
            all[game.AppId] = fresh;
            Write(SummaryPath, all);
        }
    }

    public void AppendLog(UnlockLogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, StoreJson.Compact);
        lock (_logLock) File.AppendAllText(LogPath, line + Environment.NewLine);
    }

    public IReadOnlyList<UnlockLogEntry> ReadLog(uint? appId = null)
    {
        if (!File.Exists(LogPath)) return Array.Empty<UnlockLogEntry>();
        var list = new List<UnlockLogEntry>();
        lock (_logLock)
        {
            foreach (var line in File.ReadLines(LogPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<UnlockLogEntry>(line, StoreJson.Compact);
                    if (e != null && (appId is null || e.AppId == appId)) list.Add(e);
                }
                catch (JsonException) { /* skip a broken line */ }
            }
        }
        return list;
    }

    private static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(fs, StoreJson.Pretty);
        }
        catch (JsonException)
        {
            // The file is corrupted: keep a copy so manual marks are not lost, and start clean.
            try { File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Writes through a temp file, flushed to the disk before it replaces the old one: neither a failure nor a power
    /// cut right after the save leaves a truncated or empty file.
    /// </summary>
    private static void Write<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            JsonSerializer.Serialize(fs, value, StoreJson.Pretty);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

public sealed class UnlockLogEntry
{
    public DateTimeOffset At { get; set; }
    public uint AppId { get; set; }
    public string ApiName { get; set; } = "";
    /// <summary>unlock or lock.</summary>
    public string Action { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

/// <summary>Game progress for the library list.</summary>
public sealed record GameSummary
{
    /// <summary>Summaries of an older format lack fields or count differently and are rebuilt from the game files.</summary>
    public const int CurrentFormat = 4;

    public int Format { get; init; }
    public int Unlocked { get; init; }
    public int Manual { get; init; }
    public int Total { get; init; }
    /// <summary>Typical time left to 100%; null - finished or no estimate.</summary>
    public double? RemainingMinutes { get; init; }

    /// <param name="played">Minutes played since the last unlock; null - as far as the playtime history tells.</param>
    public static GameSummary Of(GameData game, double? played = null)
    {
        var (u, m, total) = GameLogic.Progress(game);
        var left = Planner.Build(game, Percentile.P50).RemainingAfter(played ?? Progress.MinutesSinceProgress(game, DateTimeOffset.Now))?.Minutes;
        return new GameSummary { Format = CurrentFormat, Unlocked = u, Manual = m, Total = total, RemainingMinutes = left is { } v ? Math.Round(v) : null };
    }

    /// <summary>Closest to 100% first: by the time left, then games without an estimate, finished ones last.</summary>
    public static int CompareByTimeLeft(GameSummary? a, GameSummary? b)
    {
        static int Rank(GameSummary? s) => s is not { Total: > 0 } ? 1 : s.Unlocked >= s.Total ? 2 : s.RemainingMinutes is null ? 1 : 0;
        int c = Rank(a).CompareTo(Rank(b));
        return c != 0 ? c : Nullable.Compare(a?.RemainingMinutes, b?.RemainingMinutes);
    }
}

public sealed class OwnedGame
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    public int PlaytimeMinutes { get; set; }
    /// <summary>Played in the last two weeks.</summary>
    public int Playtime2WeeksMinutes { get; set; }
    public string IconHash { get; set; } = "";
    public DateTimeOffset? LastPlayed { get; set; }

    /// <summary>Steam store game header (460x215).</summary>
    [JsonIgnore]
    public string HeaderUrl => SteamImages.Header(AppId);

    [JsonIgnore]
    public string? IconUrl => string.IsNullOrEmpty(IconHash) ? null :
        $"https://media.steampowered.com/steamcommunity/public/images/apps/{AppId}/{IconHash}.jpg";
}

internal static class StoreJson
{
    public static readonly JsonSerializerOptions Pretty = new(JsonOptions.Pretty);
    public static readonly JsonSerializerOptions Compact = new(JsonOptions.Pretty) { WriteIndented = false };
}

public static class SteamImages
{
    /// <summary>Wide Steam library image (the header background).</summary>
    public static string Hero(uint appId) => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_hero.jpg";

    public static string Header(uint appId) => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
}
