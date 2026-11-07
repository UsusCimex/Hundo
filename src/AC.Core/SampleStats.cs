namespace AC.Core;

/// <summary>One sampled player: what they unlocked when, and their playtime at known moments.</summary>
public sealed class PlayerSample
{
    public ulong SteamId { get; init; }
    /// <summary>Unlocked achievements: api name and unlock time.</summary>
    public Dictionary<string, DateTimeOffset> Unlocks { get; init; } = new();
    /// <summary>
    /// Anchor points "moment - minutes played": the review (its time and playtime at the review),
    /// the last launch (its time and total playtime). They give the playtime at each unlock.
    /// </summary>
    public List<(DateTimeOffset At, double PlayedMinutes)> Anchors { get; init; } = new();
}

public sealed record SampleResult(
    int Players,
    Dictionary<string, double> Percent,
    Dictionary<string, TimeEstimate> Times,
    double? CompletionMedianMinutes,
    int Completers)
{
    /// <summary>Each player's minutes of play at each of their unlocks: what the route model learns from.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, double>> Played { get; init; } = [];
}

/// <summary>
/// Our own statistics from a player sample (when Steam has no global percentages and sites have no completion times).
///
/// Percent = share of sampled players who unlocked the achievement.
/// Time: the minutes each player had played by each unlock (<see cref="PlayedMinutes"/>); across players the median
/// and percentiles. This is an estimate: the sample is review authors, who usually play more than average.
/// </summary>
public static class SampleStats
{
    public const string SourceName = "sample";

    public static SampleResult Compute(IReadOnlyList<PlayerSample> players, IReadOnlyCollection<string> allApiNames)
    {
        var percent = new Dictionary<string, double>(StringComparer.Ordinal);
        var minutes = allApiNames.ToDictionary(n => n, _ => new List<double>(), StringComparer.Ordinal);
        var completions = new List<double>();
        int n = players.Count;
        var played = PlayedMinutes(players);

        foreach (var p in played)
        {
            foreach (var (name, m) in p)
                if (minutes.TryGetValue(name, out var list)) list.Add(m);
            if (allApiNames.Count > 0 && allApiNames.All(p.ContainsKey)) completions.Add(p.Values.Max());
        }

        foreach (var name in allApiNames)
        {
            int unlocked = players.Count(p => p.Unlocks.ContainsKey(name));
            if (n > 0) percent[name] = Math.Round(100.0 * unlocked / n, 1);
        }

        var times = new Dictionary<string, TimeEstimate>(StringComparer.Ordinal);
        foreach (var (name, list) in minutes)
        {
            if (list.Count < 2) continue;
            list.Sort();
            times[name] = new TimeEstimate
            {
                Source = SourceName,
                P50 = Math.Round(Quantile(list, 0.50), 1),
                P75 = Math.Round(Quantile(list, 0.75), 1),
                P90 = Math.Round(Quantile(list, 0.90), 1),
                SampleSize = list.Count,
            };
        }

        completions.Sort();
        double? completion = completions.Count >= 2 ? Math.Round(Quantile(completions, 0.5), 0) : null;
        return new SampleResult(n, percent, times, completion, completions.Count) { Played = played };
    }

    /// <summary>A longer pause between two unlocks is a break between sessions, not play.</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromHours(2);

    /// <summary>The play counted for a break: the start of the next session before its first unlock.</summary>
    public const double BridgeMinutes = 20;

    /// <summary>
    /// Steam's minutes per minute between unlocks when no anchor tells: the play before a session's first unlock and
    /// after its last one does not show between unlocks.
    /// </summary>
    public const double DefaultScale = 1.5;

    /// <summary>An anchor this soon after an unlock tells how much the player had played by it.</summary>
    private static readonly TimeSpan AnchorNear = TimeSpan.FromHours(3);

    /// <summary>
    /// Minutes each player had played by each unlock, 0 at the first one. Within a session the time between unlocks
    /// was played; a break counts <see cref="BridgeMinutes"/>: calendar time would count the nights and weeks between
    /// sessions. An anchor soon after an unlock scales the player's minutes to what Steam counted; without one, the
    /// sample's median scale.
    /// </summary>
    public static List<IReadOnlyDictionary<string, double>> PlayedMinutes(IReadOnlyList<PlayerSample> players)
    {
        var sessions = players.Select(p => Sessions(p.Unlocks)).ToList();
        var scales = players.Select((p, i) => ScaleOf(p, sessions[i])).ToList();
        var known = scales.OfType<double>().OrderBy(k => k).ToList();
        double common = known.Count > 0 ? Math.Max(1, Quantile(known, 0.5)) : DefaultScale;
        return sessions.Select((m, i) => (IReadOnlyDictionary<string, double>)m.ToDictionary(
            kv => kv.Key, kv => Math.Round(kv.Value * (scales[i] ?? common), 1), StringComparer.Ordinal)).ToList();
    }

    /// <summary>Minutes between unlocks, sessions apart joined by <see cref="BridgeMinutes"/>.</summary>
    private static Dictionary<string, double> Sessions(IReadOnlyDictionary<string, DateTimeOffset> unlocks)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        double total = 0;
        DateTimeOffset? previous = null;
        foreach (var (name, at) in unlocks.OrderBy(kv => kv.Value))
        {
            if (previous is { } p) total += at - p <= SessionGap ? (at - p).TotalMinutes : BridgeMinutes;
            result[name] = total;
            previous = at;
        }
        return result;
    }

    /// <summary>Steam's minutes per counted minute, from the latest anchor soon after an unlock; null - none.</summary>
    private static double? ScaleOf(PlayerSample p, Dictionary<string, double> sessions)
    {
        double? scale = null;
        foreach (var (at, minutes) in p.Anchors.Where(a => a.PlayedMinutes > 0).OrderBy(a => a.At))
        {
            var before = p.Unlocks.Where(kv => kv.Value <= at).OrderBy(kv => kv.Value).LastOrDefault();
            if (before.Key is null || at - before.Value > AnchorNear) continue;
            double counted = sessions[before.Key] + (at - before.Value).TotalMinutes;
            if (counted >= 30) scale = minutes / counted;
        }
        return scale;
    }

    private static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 1) return sorted[0];
        double pos = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    /// <summary>Writes the result into the game: sample percentages, measured times, time to 100%.</summary>
    public static void Apply(GameData game, SampleResult r, DateTimeOffset now)
    {
        foreach (var a in game.Achievements)
            a.SamplePercent = r.Percent.TryGetValue(a.ApiName, out var pc) ? pc : null;
        foreach (var key in game.Estimates.Where(kv => kv.Value.Source == SourceName).Select(kv => kv.Key).ToList())
            game.Estimates.Remove(key);
        foreach (var (name, est) in r.Times) game.Estimates[name] = est;
        game.SampleCompletionMinutes = r.CompletionMedianMinutes;
        game.SamplePlayers = r.Players;
        game.SampleCompleters = r.Completers;
        game.SampleFetchedAt = now;
        game.Route = RouteModel.Build(game, r.Played);
    }
}
