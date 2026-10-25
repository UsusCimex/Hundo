namespace AC.Core;

/// <summary>A play session seen from its start: when the game was noticed running and when it closed.</summary>
public sealed record PlaySession(uint AppId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Follows the running game from tick to tick: reports the sessions seen from their start and keeps all play seen,
/// since Steam's playtime shows a session only after it ends.
/// </summary>
public sealed class SessionTracker(uint runningAtStart, DateTimeOffset startedAt)
{
    private uint _running = runningAtStart;
    // A game already running when the app started has no known start, so its session is not reported.
    private DateTimeOffset? _start;
    // Since when the running game is known to run: its start, or the app's.
    private DateTimeOffset _since = startedAt;
    private readonly List<PlaySession> _played = new();

    /// <summary>The game running now (0 - none). Returns the session that has just ended.</summary>
    public PlaySession? Observe(uint running, DateTimeOffset now)
    {
        if (running == _running) return null;
        if (_running != 0) _played.Add(new PlaySession(_running, _since, now));
        var ended = _running != 0 && _start is { } start ? new PlaySession(_running, start, now) : null;
        _running = running;
        _start = running != 0 ? now : null;
        _since = now;
        return ended;
    }

    /// <summary>The play of a game seen since the app started, the running session up to now included.</summary>
    public IReadOnlyList<PlaySession> Played(uint appId, DateTimeOffset now) =>
        _played.Where(s => s.AppId == appId)
            .Concat(_running == appId ? [new PlaySession(appId, _since, now)] : [])
            .ToList();
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
