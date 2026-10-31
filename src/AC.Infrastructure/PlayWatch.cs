using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

/// <summary>The games started and stopped since the changes were last taken.</summary>
public sealed record PlayChanges(IReadOnlyList<uint> Started, IReadOnlyList<uint> Stopped);

/// <summary>
/// Looks up the running games off the UI thread and follows the sessions played in them. The changes between looks
/// wait until the caller can act on them, so a game started and closed while it was busy is not lost.
/// </summary>
/// <param name="helperUses">The app uses the game through the Steam client (<see cref="BridgeClient.InUse"/>).</param>
[SupportedOSPlatform("windows")]
public sealed class PlayWatch(Func<IReadOnlySet<uint>> scan, Func<DateTimeOffset> clock, Func<uint, bool> helperUses)
{
    private bool _scanning;
    private bool _scanFailed;
    private IReadOnlySet<uint> _running = new HashSet<uint>();
    private IReadOnlySet<uint> _taken = new HashSet<uint>();
    private readonly List<PlaySession> _ended = new();
    private readonly Dictionary<uint, PlaySession> _lastSessions = new();

    public PlayWatch() : this(SteamLocal.RunningAppIds, () => DateTimeOffset.Now, BridgeClient.InUse) { }

    /// <summary>Started over by the first look, when the window opens.</summary>
    public SessionTracker Sessions { get; private set; } = new([], clock());

    /// <summary>The first look. The games already running: their sessions began earlier and are not reported.</summary>
    public async Task<IReadOnlySet<uint>> StartAsync()
    {
        var running = await ScanAsync() ?? new HashSet<uint>();
        Sessions = new SessionTracker(running, clock());
        return running;
    }

    /// <summary>A look at the running games; null - the previous look still runs, or this one failed (logged once).</summary>
    public async Task<IReadOnlySet<uint>?> LookAsync()
    {
        if (_scanning || await ScanAsync() is not { } scanned) return null;
        // While the app reads or writes a game through the Steam client, Steam names that game as running: not a start
        // of the game, unless it was running already.
        IReadOnlySet<uint> running = scanned.Where(a => _running.Contains(a) || !helperUses(a)).ToHashSet();
        // Every look, whether the changes are taken or not: starts and ends are stamped when seen, and a pause between
        // looks is taken for sleep.
        _ended.AddRange(Sessions.Observe(running, clock()));
        _running = running;
        return running;
    }

    public bool HasChanges => !_running.SetEquals(_taken) || _ended.Count > 0;

    /// <summary>
    /// The games started and stopped since the last call. A session that ended becomes the last one of its game, and
    /// a game started again has none.
    /// </summary>
    public PlayChanges TakeChanges()
    {
        var changes = new PlayChanges(_running.Except(_taken).ToList(), _taken.Except(_running).ToList());
        foreach (var session in _ended) _lastSessions[session.AppId] = session;
        foreach (var app in changes.Started) _lastSessions.Remove(app);
        _ended.Clear();
        _taken = _running;
        return changes;
    }

    /// <summary>The last session of a game seen from its start: it stays until dismissed or the game starts again.</summary>
    public PlaySession? LastSession(uint appId) => _lastSessions.GetValueOrDefault(appId);

    public void Dismiss(uint appId) => _lastSessions.Remove(appId);

    /// <summary>
    /// Of the games started, the one to open: an installed game before one known only from Steam's registry, such as
    /// Spacewar started by an online fix next to the game; of installed ones, the one Steam names.
    /// </summary>
    public static uint Followed(IReadOnlyCollection<uint> started)
    {
        var installed = SteamLocal.InstalledGames();
        uint named = SteamLocal.NamedAppId();
        return started.OrderByDescending(a => installed.ContainsKey(a)).ThenByDescending(a => a == named).First();
    }

    /// <summary>
    /// The scan opens every process and, every few minutes, reads the app manifests of all libraries. null - it
    /// failed.
    /// </summary>
    private async Task<IReadOnlySet<uint>?> ScanAsync()
    {
        _scanning = true;
        try
        {
            var running = await Task.Run(scan);
            _scanFailed = false;
            return running;
        }
        catch (Exception e)
        {
            if (!_scanFailed) AppLog.Error("Running games", e);
            _scanFailed = true;
            return null;
        }
        finally
        {
            _scanning = false;
        }
    }
}
