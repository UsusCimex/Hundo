namespace AC.Core;

public enum PlanStage
{
    Missable = 1,
    Story = 2,
    Other = 3,
    Grind = 4,
}

/// <summary>A "can be done in one sitting" group.</summary>
public sealed record PlanGroup(string Label, IReadOnlyList<AchievementRow> Items, double? LongestMinutes);

public sealed record PlanSection(PlanStage Stage, IReadOnlyList<PlanGroup> Groups)
{
    public int Count => Groups.Sum(g => g.Items.Count);
}

/// <param name="Pace">How many times slower than the median the player is (1 - not slower, or unknown).</param>
public sealed record Plan(
    IReadOnlyList<PlanSection> Sections,
    double? RemainingMinutes,
    int UnknownEstimateCount,
    double Pace = 1)
{
    /// <summary>Flat playthrough order.</summary>
    public IEnumerable<AchievementRow> Ordered => Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items);

    /// <summary>Nothing left to unlock.</summary>
    public bool IsComplete => Sections.Count == 0;

    /// <summary>Time left to each locked achievement with an estimate (<see cref="Planner.TimeLeft"/>).</summary>
    public IReadOnlyDictionary<string, double> TimeLeft { get; init; } = new Dictionary<string, double>();

    /// <summary>The same with the wide margin (P90): the time a player who takes longer gets.</summary>
    public IReadOnlyDictionary<string, double> WideTimeLeft { get; init; } = new Dictionary<string, double>();

    /// <summary>
    /// Time left to each locked achievement after <paramref name="played"/> minutes of play since the last unlock.
    /// Past the estimate the player takes longer than it assumes, and the wide margin gives more time; past that too,
    /// there is no estimate.
    /// </summary>
    public Dictionary<string, Countdown> CountDown(double played)
    {
        var result = new Dictionary<string, Countdown>(StringComparer.Ordinal);
        foreach (var (name, left) in TimeLeft)
        {
            double wide = Math.Max(left + Planner.MinMarginMinutes, WideTimeLeft.GetValueOrDefault(name, left));
            result[name] = played < left ? new Countdown(left - played, false)
                : new Countdown(played < wide ? wide - played : null, true);
        }
        return result;
    }

    /// <summary>The time to 100% after that play: the longest countdown; null - nothing to count.</summary>
    public Countdown? RemainingAfter(double played)
    {
        if (TimeLeft.Count == 0) return null;
        var known = CountDown(played).Values.Where(c => c.Minutes.HasValue).ToList();
        return known.Count == 0 ? new Countdown(null, true) : known.MaxBy(c => c.Minutes!.Value);
    }
}

/// <summary>Time left to an achievement at the moment.</summary>
/// <param name="Minutes">null - longer than even the wide margin.</param>
/// <param name="Late">Past the estimate: the time left is counted with the wide margin.</param>
public readonly record struct Countdown(double? Minutes, bool Late);

/// <summary>
/// Order planner.
/// 1) missable, 2) story, 3) other, 4) grind. Within a stage - by time estimate from the start of the game,
/// without an estimate - by global % descending. Groups by the Group label.
/// </summary>
public static class Planner
{
    /// <summary>
    /// How much slower than the median a player may be for us to still trust their pace.
    /// Beyond that it is more likely AFK or hours before achievements existed (fixed on the "Time" tab).
    /// </summary>
    public const double MaxPace = 3;

    /// <summary>
    /// The wide margin past an estimate is at least this long: the rarity model puts the most common achievement at
    /// the very start, and neither the estimates nor Steam's minutes are finer.
    /// </summary>
    public const double MinMarginMinutes = 15;

    public static Plan Build(GameData game, Percentile p, DateTimeOffset? now = null)
    {
        var rows = GameLogic.BuildRows(game, p);
        var open = rows.Where(r => !r.IsDone).ToList();

        var sections = new List<PlanSection>();
        foreach (var stage in new[] { PlanStage.Missable, PlanStage.Story, PlanStage.Other, PlanStage.Grind })
        {
            var items = open.Where(r => StageOf(r.Kind) == stage).ToList();
            if (items.Count == 0) continue;

            var groups = items
                .GroupBy(r => r.Group.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var ordered = g.OrderBy(r => r, RowOrder.Instance).ToList();
                    var longest = ordered.Where(r => r.EstimateMinutes.HasValue).Select(r => r.EstimateMinutes!.Value).DefaultIfEmpty().Max();
                    return new PlanGroup(g.Key, ordered, ordered.Any(r => r.EstimateMinutes.HasValue) ? longest : null);
                })
                // groups are ordered by their earliest item
                .OrderBy(g => g.Items[0], RowOrder.Instance)
                .ToList();

            sections.Add(new PlanSection(stage, groups));
        }

        double pace = PaceOf(game, now ?? DateTimeOffset.Now);
        int unknown = open.Count(r => r.EstimateMinutes is null);
        var left = TimeLeft(rows, p, pace);
        var wide = p == Percentile.P90 ? left : TimeLeft(GameLogic.BuildRows(game, Percentile.P90), Percentile.P90, pace);
        return new Plan(sections, left.Count == 0 ? null : left.Values.Max(), unknown, pace) { TimeLeft = left, WideTimeLeft = wide };
    }

    /// <summary>
    /// How many times slower than the median the player is, measured on the median estimates by own unlocks
    /// (<see cref="Progress.MinutesPerUnit"/>), so idle playtime does not count. Needs progress worth a tenth of
    /// the game or half an hour. Faster than the median gives no speed-up (a margin does not hurt),
    /// slower is capped at MaxPace.
    /// </summary>
    public static double PaceOf(GameData game, DateTimeOffset now)
    {
        var typical = game.Estimates.Values.Select(e => e.P50 ?? 0).DefaultIfEmpty().Max();
        var own = Progress.MinutesPerUnit(game, name => game.Estimates.GetValueOrDefault(name)?.P50, now,
            minProgress: Math.Max(30, typical / 10));
        return own is { } o ? Math.Clamp(o, 1, MaxPace) : 1;
    }

    public static PlanStage StageOf(AchievementKind kind) => kind switch
    {
        AchievementKind.Missable => PlanStage.Missable,
        AchievementKind.Story => PlanStage.Story,
        AchievementKind.Grind => PlanStage.Grind,
        _ => PlanStage.Other,
    };

    /// <summary>
    /// Time left to each locked achievement: the stretches (<see cref="Progress.Stretches"/>) of the locked ones up
    /// to it, at the player's pace. Played in order, that is the way from the last unlock to it; the last one of
    /// all is the time to 100%. Unlocks further on do not shorten the way to an earlier one.
    /// Computed on the scale of the median player at the chosen percentile: own estimates are divided by pace
    /// and get the same margin as the others, then everything is multiplied by pace.
    /// </summary>
    public static Dictionary<string, double> TimeLeft(IReadOnlyList<AchievementRow> rows, Percentile p, double pace)
    {
        double margin = p switch
        {
            Percentile.P75 => TimeEstimator.P75Factor,
            Percentile.P90 => TimeEstimator.P90Factor,
            _ => 1,
        };
        var at = rows.Where(r => r.EstimateMinutes.HasValue).ToDictionary(r => r.ApiName,
            r => r.EstimateIsManual ? r.EstimateMinutes!.Value / pace * margin : r.EstimateMinutes!.Value, StringComparer.Ordinal);
        var stretches = Progress.Stretches(at.Select(kv => (kv.Key, kv.Value)));

        var left = new Dictionary<string, double>(StringComparer.Ordinal);
        double way = 0;
        foreach (var point in rows.Where(r => !r.IsDone && at.ContainsKey(r.ApiName)).GroupBy(r => at[r.ApiName]).OrderBy(g => g.Key))
        {
            way += point.Sum(r => stretches[r.ApiName]);
            foreach (var r in point) left[r.ApiName] = way * pace;
        }
        return left;
    }

    private sealed class RowOrder : IComparer<AchievementRow>
    {
        public static readonly RowOrder Instance = new();

        public int Compare(AchievementRow? x, AchievementRow? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            // estimated before unestimated
            if (x.EstimateMinutes.HasValue != y.EstimateMinutes.HasValue)
                return x.EstimateMinutes.HasValue ? -1 : 1;
            if (x.EstimateMinutes.HasValue)
            {
                int c = x.EstimateMinutes!.Value.CompareTo(y.EstimateMinutes!.Value);
                if (c != 0) return c;
            }
            // common before rare
            int pc = (y.Def.Percent ?? -1).CompareTo(x.Def.Percent ?? -1);
            if (pc != 0) return pc;
            return string.CompareOrdinal(x.ApiName, y.ApiName);
        }
    }
}
