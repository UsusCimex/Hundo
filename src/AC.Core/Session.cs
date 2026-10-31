namespace AC.Core;

/// <summary>A play session seen from its start: when the game was noticed running and when it closed.</summary>
public sealed record PlaySession(uint AppId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Follows the running games from tick to tick, each on its own: reports the sessions seen from their start and keeps
/// all play seen, since Steam's playtime shows a session late.
/// </summary>
public sealed class SessionTracker
{
    // Per running game: its start, unknown for one already running when the app started (its session is not
    // reported), and since when it is known to run.
    private readonly Dictionary<uint, (DateTimeOffset? Start, DateTimeOffset Since)> _running = new();
    private readonly List<PlaySession> _played = new();

    public SessionTracker(IEnumerable<uint> runningAtStart, DateTimeOffset startedAt)
    {
        foreach (var app in runningAtStart.Where(a => a != 0)) _running[app] = (null, startedAt);
    }

    /// <summary>The games running now. Returns the sessions that have just ended.</summary>
    public IReadOnlyList<PlaySession> Observe(IReadOnlyCollection<uint> running, DateTimeOffset now)
    {
        var ended = new List<PlaySession>();
        foreach (var (app, (start, since)) in _running.Where(r => !running.Contains(r.Key)).ToList())
        {
            _played.Add(new PlaySession(app, since, now));
            if (start is { } s) ended.Add(new PlaySession(app, s, now));
            _running.Remove(app);
        }
        foreach (var app in running.Where(a => a != 0 && !_running.ContainsKey(a))) _running[app] = (now, now);
        return ended;
    }

    public bool IsRunning(uint appId) => _running.ContainsKey(appId);

    /// <summary>How long the game has been seen running; null - it is not running.</summary>
    public TimeSpan? RunningFor(uint appId, DateTimeOffset now) => _running.TryGetValue(appId, out var r) ? now - r.Since : null;

    /// <summary>The play of a game seen since the app started, the running session up to now included.</summary>
    public IReadOnlyList<PlaySession> Played(uint appId, DateTimeOffset now) =>
        _played.Where(s => s.AppId == appId)
            .Concat(_running.TryGetValue(appId, out var r) ? [new PlaySession(appId, r.Since, now)] : [])
            .ToList();
}

public static class RunningGames
{
    /// <summary>
    /// Installed games with a process started from their folder. Steam names only one running game, and with two at
    /// once (a game and Spacewar started by an online fix) the other one would go unnoticed.
    /// </summary>
    public static HashSet<uint> ByProcess(IEnumerable<string> processPaths, IReadOnlyDictionary<uint, string> installDirs)
    {
        var paths = processPaths.ToList();
        var running = new HashSet<uint>();
        foreach (var (app, dir) in installDirs)
        {
            var folder = dir.TrimEnd('\\', '/') + '\\';
            if (paths.Any(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase))) running.Add(app);
        }
        return running;
    }
}

/// <summary>What a session did: its unlocks in order and the time to 100% without and with them.</summary>
public sealed record SessionSummary(
    TimeSpan Duration,
    IReadOnlyList<AchievementDef> Unlocked,
    double? LeftBefore,
    double? LeftAfter,
    bool IsComplete)
{
    /// <summary>Steam's unlock times and the local clock may differ a little.</summary>
    private static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The time to 100% counts down with the play since the last unlock (<see cref="Progress.MinutesSinceProgress"/>),
    /// so before the session it is taken without its unlocks and without its play.
    /// </summary>
    /// <param name="played">The play the app saw (<see cref="SessionTracker.Played"/>).</param>
    public static SessionSummary Of(GameData game, PlaySession session, Percentile p, DateTimeOffset now, IReadOnlyList<PlaySession> played)
    {
        var unlocked = game.Achievements
            .Select(a => (Def: a, State: game.States.GetValueOrDefault(a.ApiName)))
            .Where(x => x.State is { SteamUnlocked: true, SteamUnlockTime: { } t } &&
                t >= session.Start - ClockSlack && t <= session.End + ClockSlack)
            .OrderBy(x => x.State!.SteamUnlockTime)
            .ToList();
        var plan = Planner.Build(game, p);
        var after = plan.RemainingAfter(Progress.MinutesSinceProgress(game, now, played));
        foreach (var x in unlocked) x.State!.SteamUnlocked = false;
        Countdown? before;
        try
        {
            // Without its unlocks the play since the previous one runs through the whole session.
            double sincePrevious = Progress.MinutesSinceProgress(game, now, played);
            before = Planner.Build(game, p).RemainingAfter(Math.Max(0, sincePrevious - (session.End - session.Start).TotalMinutes));
        }
        finally { foreach (var x in unlocked) x.State!.SteamUnlocked = true; }
        return new SessionSummary(session.End - session.Start, unlocked.Select(x => x.Def).ToList(),
            before?.Minutes, after?.Minutes, plan.IsComplete);
    }
}
