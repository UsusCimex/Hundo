namespace AC.Core;

/// <summary>
/// How far the player has come and how fast they go, judged by what they unlocked rather than by raw playtime,
/// which also counts AFK, idling and a game left open. On a scale such as "minutes from the start" every achievement
/// owns the stretch from the previous one to it: the stretches of unlocked ones are the progress made, the stretches
/// of locked ones are what is left, whatever the order of unlocks.
/// </summary>
public static class Progress
{
    /// <summary>The stretch each achievement owns; achievements at the same point share it.</summary>
    public static Dictionary<string, double> Stretches(IEnumerable<(string Name, double At)> achievements)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        double previous = 0;
        foreach (var point in achievements.GroupBy(a => a.At).OrderBy(g => g.Key))
        {
            var names = point.Select(a => a.Name).ToList();
            double share = Math.Max(0, point.Key - previous) / names.Count;
            foreach (var name in names) result[name] = share;
            previous = Math.Max(previous, point.Key);
        }
        return result;
    }

    /// <summary>
    /// Locked achievements the player has gone past: they are clearly before a done one on the scale, and two thirds
    /// of the achievements up to that one are done. A single rare one done early moves nothing.
    /// </summary>
    /// <param name="clearly">How far before that point an achievement has to be: nearby ones come about together.</param>
    public static HashSet<string> Behind(IEnumerable<(string Name, double At, bool Done)> achievements, double clearly)
    {
        var points = achievements.GroupBy(a => a.At).OrderBy(g => g.Key).ToList();
        double reached = double.NegativeInfinity;
        int seen = 0, done = 0;
        foreach (var point in points)
        {
            seen += point.Count();
            done += point.Count(a => a.Done);
            if (point.Any(a => a.Done) && done * 3 >= seen * 2) reached = point.Key;
        }
        return points.Where(p => p.Key < reached - clearly).SelectMany(p => p)
            .Where(a => !a.Done).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Own minutes of play per unit of the scale; on the "minutes from the start" scale of a typical player,
    /// 2 means twice as slow. Playtime between two known moments is matched with the progress made between them,
    /// and the result is the median weighted by progress. Stretches with playtime but no unlock (AFK, idling)
    /// are left out entirely. null - less progress than <paramref name="minProgress"/> to measure by.
    /// </summary>
    public static double? MinutesPerUnit(GameData game, Func<string, double?> scale, DateTimeOffset now, double minProgress)
    {
        var stretches = Stretches(game.Achievements
            .Select(a => (a.ApiName, At: scale(a.ApiName)))
            .Where(a => a.At.HasValue)
            .Select(a => (a.ApiName, a.At!.Value)));
        var done = new List<(DateTimeOffset At, double Stretch)>();
        foreach (var (name, stretch) in stretches)
        {
            var state = game.States.GetValueOrDefault(name);
            DateTimeOffset? at = GameLogic.StatusOf(state, game.CountManualMarks) switch
            {
                EffectiveStatus.Unlocked => state!.SteamUnlockTime ?? DateTimeOffset.MinValue,
                EffectiveStatus.ManualDone => state!.ManualAt ?? DateTimeOffset.MinValue,
                _ => null,
            };
            if (at is { } t) done.Add((t, stretch));
        }

        var segments = new List<(double Pace, double Weight)>();
        double lastMinutes = 0, lastProgress = 0;
        foreach (var point in Points(game, now, []))
        {
            double progress = done.Where(d => d.At <= point.At).Sum(d => d.Stretch);
            double played = point.Minutes - lastMinutes, progressed = progress - lastProgress;
            if (progressed <= 0)
            {
                lastMinutes = point.Minutes;
                continue;
            }
            // Progress seen before its playtime: it goes to the next stretch.
            if (played <= 0) continue;
            segments.Add((played / progressed, progressed));
            lastMinutes = point.Minutes;
            lastProgress = progress;
        }
        return segments.Sum(s => s.Weight) >= minProgress ? WeightedMedian(segments) : null;
    }

    /// <summary>
    /// Minutes played since the last unlock or mark, from the start of the game when there is none. Only play known to
    /// come after it counts: between two known moments the play is taken to start as early as it can.
    /// </summary>
    /// <param name="played">The play the app saw (<see cref="SessionTracker.Played"/>): Steam's playtime shows a session only after it ends.</param>
    public static double MinutesSinceProgress(GameData game, DateTimeOffset now, IReadOnlyList<PlaySession>? played = null)
    {
        DateTimeOffset? last = null;
        foreach (var a in game.Achievements)
        {
            var state = game.States.GetValueOrDefault(a.ApiName);
            DateTimeOffset? at = GameLogic.StatusOf(state, game.CountManualMarks) switch
            {
                EffectiveStatus.Unlocked => state!.SteamUnlockTime ?? DateTimeOffset.MinValue,
                EffectiveStatus.ManualDone => state!.ManualAt ?? DateTimeOffset.MinValue,
                _ => null,
            };
            if (at is { } t && (last is null || t > last)) last = t;
        }

        var points = Points(game, now, played ?? []);
        double total = points[^1].Minutes;
        if (last is not { } u) return total;
        double then = total;
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].At < u) continue;
            then = i == 0 ? points[0].Minutes : Math.Min(points[i - 1].Minutes + (u - points[i - 1].At).TotalMinutes, points[i].Minutes);
            break;
        }
        return Math.Max(0, total - then);
    }

    /// <summary>
    /// Adds the start of a session the app saw to the history, now that Steam's playtime shows it: the playtime after
    /// it less its length. Steam rounds to minutes; a session it has not counted yet does not fit the history and is skipped.
    /// </summary>
    public static void AddSessionStart(GameData game, PlaySession session, double minutesAfter)
    {
        double start = minutesAfter - (session.End - session.Start).TotalMinutes;
        var before = game.PlaytimeHistory.LastOrDefault(p => p.At < session.Start);
        if (before is not null && before.Minutes > start && before.Minutes - start <= 2) start = before.Minutes;
        AddPlaytime(game, session.Start, start);
    }

    /// <summary>Points kept per game: years of play between sessions.</summary>
    public const int MaxPlaytimePoints = 300;

    /// <summary>
    /// Adds a playtime point to the history, oldest first. A point that contradicts its neighbours is skipped
    /// (Steam playtime only grows); of a stretch with the same playtime only its first and last points stay.
    /// </summary>
    public static void AddPlaytime(GameData game, DateTimeOffset at, double minutes)
    {
        var history = game.PlaytimeHistory;
        int i = history.FindIndex(p => p.At > at);
        if (i < 0) i = history.Count;
        if (i > 0 && history[i - 1].Minutes > minutes) return;
        if (i < history.Count && history[i].Minutes < minutes) return;
        history.Insert(i, new PlaytimePoint { At = at, Minutes = minutes });
        for (int k = history.Count - 2; k >= 1; k--)
            if (history[k - 1].Minutes == history[k].Minutes && history[k + 1].Minutes == history[k].Minutes)
                history.RemoveAt(k);
        if (history.Count > MaxPlaytimePoints) history.RemoveRange(0, history.Count - MaxPlaytimePoints);
    }

    /// <summary>
    /// Known playtime moments, oldest first, ending with the current playtime; manual corrections applied.
    /// Sessions after the last point of the history go on top of it. Steam's playtime may show part of a running
    /// session already, so only what it shows beyond them is play the app did not see, taken to come first.
    /// </summary>
    private static List<PlaytimePoint> Points(GameData game, DateTimeOffset now, IReadOnlyList<PlaySession> played)
    {
        var raw = new List<PlaytimePoint>();
        if (game.PlaytimeOverrideMinutes is null) raw.AddRange(game.PlaytimeHistory.Where(p => p.At < now).OrderBy(p => p.At));
        double counted = game.PlaytimeOverrideMinutes ?? game.PlaytimeMinutes, extra = 0;
        var since = raw.Count > 0 ? raw[^1].At : DateTimeOffset.MinValue;
        var sessions = played.Where(s => s.End > since && s.Start < now).OrderBy(s => s.Start)
            .Select(s => (Start: s.Start > since ? s.Start : since, End: s.End < now ? s.End : now)).ToList();
        double sessionMinutes = sessions.Sum(s => (s.End - s.Start).TotalMinutes);
        double from = raw.Count > 0 ? Math.Max(raw[^1].Minutes, counted - sessionMinutes) : counted;
        foreach (var (start, end) in sessions)
        {
            raw.Add(new PlaytimePoint { At = start, Minutes = from + extra });
            extra += (end - start).TotalMinutes;
            raw.Add(new PlaytimePoint { At = end, Minutes = from + extra });
        }
        raw.Add(new PlaytimePoint { At = now, Minutes = from + extra });

        double offset = game.PlaytimeOverrideMinutes is null ? game.PlaytimeOffsetMinutes : 0, seen = 0;
        var points = new List<PlaytimePoint>(raw.Count);
        foreach (var p in raw)
        {
            // Steam playtime only grows; the hours to ignore are taken from the oldest ones.
            seen = Math.Max(seen, p.Minutes);
            points.Add(new PlaytimePoint { At = p.At, Minutes = Math.Max(0, seen - offset) });
        }
        return points;
    }

    private static double? WeightedMedian(List<(double Value, double Weight)> items)
    {
        if (items.Count == 0) return null;
        items.Sort((a, b) => a.Value.CompareTo(b.Value));
        double half = items.Sum(i => i.Weight) / 2, sum = 0;
        foreach (var (value, weight) in items)
        {
            sum += weight;
            if (sum >= half) return value;
        }
        return items[^1].Value;
    }
}
