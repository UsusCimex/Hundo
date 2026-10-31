using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AC.Core;
using AC.Core.SteamFormats;
using Microsoft.Win32;

namespace AC.Infrastructure;

/// <summary>
/// Reads local Steam offline, no game process access: registry, loginusers.vdf, libraries and app manifests,
/// appcache/stats, and the program files of running processes.
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
    /// The games running now: the one Steam names in the registry and the installed ones with a process started from
    /// their folder (<see cref="RunningGames.ByProcess"/>). Steam names only one, and its per-app Running flags stay
    /// set after a game closes. AC_RUNNING_APPID ("480,2315050") makes the app take those games for the only running
    /// ones: to check what happens during play without a game.
    /// </summary>
    public static IReadOnlySet<uint> RunningAppIds()
    {
        if (Environment.GetEnvironmentVariable("AC_RUNNING_APPID") is { } fake)
            return fake.Split(',', StringSplitOptions.TrimEntries).Select(s => uint.TryParse(s, out var id) ? id : 0).Where(id => id != 0).ToHashSet();
        uint named = NamedAppId();
        var running = RunningGames.ByProcess(ProcessPaths(), InstalledGames(), named);
        if (named != 0) running.Add(named);
        return running;
    }

    /// <summary>The game Steam names as running in the registry; 0 - none.</summary>
    public static uint NamedAppId() => Registry.GetValue(SteamKey, "RunningAppID", 0) is int id and > 0 ? (uint)id : 0;

    private sealed record InstalledCache(DateTime At, IReadOnlyDictionary<uint, string> Games);
    private static InstalledCache? _installed;
    private static readonly TimeSpan InstalledTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Installed games with achievements and their folders, from the app manifests of the Steam libraries; read again
    /// after a few minutes. Apps without an achievement schema in Steam's cache are left out: the tools that run in the
    /// background (Wallpaper Engine) or next to a game (the Steamworks redistributables at its first start).
    /// </summary>
    public static IReadOnlyDictionary<uint, string> InstalledGames()
    {
        if (_installed is { } cache && DateTime.UtcNow - cache.At < InstalledTtl) return cache.Games;
        var games = new Dictionary<uint, string>();
        if (SteamPath() is { } steam)
        {
            var stats = Path.Combine(steam, "appcache", "stats");
            var libraries = new List<string> { steam };
            try { libraries.AddRange(TextVdf.LibraryPaths(File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf")))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var apps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var file in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        if (TextVdf.AppManifest(File.ReadAllText(file)) is { } m &&
                            File.Exists(Path.Combine(stats, $"UserGameStatsSchema_{m.AppId}.bin")))
                            games[m.AppId] = Path.GetFullPath(Path.Combine(apps, "common", m.InstallDir));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        _installed = new InstalledCache(DateTime.UtcNow, games);
        return games;
    }

    /// <summary>The program files of the running processes; ones that do not let even that be read are skipped.</summary>
    private static List<string> ProcessPaths()
    {
        var paths = new List<string>();
        var name = new char[1024];
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var h = OpenProcess(ProcessQueryLimitedInformation, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    int size = name.Length;
                    if (QueryFullProcessImageNameW(h, 0, name, ref size)) paths.Add(new string(name, 0, size));
                }
                finally { CloseHandle(h); }
            }
        }
        return paths;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

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
