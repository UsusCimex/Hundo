using System.Runtime.Versioning;
using AC.Core.SteamFormats;
using Microsoft.Win32;

namespace AC.Infrastructure;

/// <summary>
/// Reads local Steam offline, no game process access: registry,
/// loginusers.vdf, appcache/stats.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SteamLocal
{
    public const ulong SteamId64Base = 76561197960265728UL;
    private const string SteamKey = @"HKEY_CURRENT_USER\Software\Valve\Steam";

    public static string? SteamPath()
    {
        var p = Registry.GetValue(SteamKey, "SteamPath", null) as string;
        if (string.IsNullOrEmpty(p))
            p = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        return string.IsNullOrEmpty(p) ? null : Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Running game AppID (Steam writes it itself), 0 - nothing is running.
    /// AC_RUNNING_APPID makes the app take that game for the running one: to check what happens during play without a game.
    /// </summary>
    public static uint RunningAppId()
    {
        if (uint.TryParse(Environment.GetEnvironmentVariable("AC_RUNNING_APPID"), out var fake)) return fake;
        var v = Registry.GetValue(SteamKey, "RunningAppID", 0);
        return v switch
        {
            int i when i > 0 => (uint)i,
            _ => 0,
        };
    }

    /// <summary>Steam UI language (russian, english...).</summary>
    public static string? ClientLanguage() => Registry.GetValue(SteamKey, "Language", null) as string;

    public static (ulong SteamId, string PersonaName)? MostRecentUser()
    {
        var steam = SteamPath();
        if (steam is null) return null;
        var path = Path.Combine(steam, "config", "loginusers.vdf");
        if (!File.Exists(path)) return null;
        try
        {
            return TextVdf.MostRecentUser(File.ReadAllText(path));
        }
        catch (IOException) { return null; }
    }

    public static string? StatsDir()
    {
        var steam = SteamPath();
        return steam is null ? null : Path.Combine(steam, "appcache", "stats");
    }

    /// <summary>Client cache schema: hidden descriptions and the "protected" flag.</summary>
    public static IReadOnlyList<SchemaAchievement>? ReadSchema(uint appId, string language)
    {
        var dir = StatsDir();
        if (dir is null) return null;
        var kv = BinaryKeyValue.Load(Path.Combine(dir, $"UserGameStatsSchema_{appId}.bin"));
        return kv is null ? null : StatsSchemaReader.Read(kv, appId, language);
    }

    public static uint AccountId(ulong steamId64) => (uint)(steamId64 - SteamId64Base);

    public static string UserStatsFileName(ulong steamId64, uint appId) => $"UserGameStats_{AccountId(steamId64)}_{appId}.bin";
}

/// <summary>
/// Watches UserGameStats_*_appid.bin: Steam rewrites it when the player's stats change.
/// The file is not parsed (undocumented format); it only signals "time to refresh the status".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StatsFileWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _debounce;
    public event Action? Changed;

    public StatsFileWatcher(ulong steamId64, uint appId)
    {
        _debounce = new Timer(_ => Changed?.Invoke(), null, Timeout.Infinite, Timeout.Infinite);
        var dir = SteamLocal.StatsDir();
        if (dir is null || !Directory.Exists(dir)) return;
        _watcher = new FileSystemWatcher(dir, SteamLocal.UserStatsFileName(steamId64, appId))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Kick();
        _watcher.Created += (_, _) => Kick();
        _watcher.Renamed += (_, _) => Kick();
    }

    public bool IsActive => _watcher is not null;

    // Steam writes the file in bursts - wait for 3 seconds of quiet.
    private void Kick() => _debounce.Change(3000, Timeout.Infinite);

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
