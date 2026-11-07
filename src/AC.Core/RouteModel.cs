namespace AC.Core;

/// <summary>
/// A story game as players go through it, learned from the player sample: the first run ends with the first
/// achievement that finishes the story; the order of the story is where achievements come in the first runs, and
/// achievements players almost never get in one run exclude each other (outcomes of one choice, or of routes apart).
/// </summary>
public sealed class RouteData
{
    /// <summary>Achievements that finish the story: the first of them a player gets ends their first run.</summary>
    public List<string> Finales { get; set; } = new();
    /// <summary>Median minutes of play when the first run ends.</summary>
    public double FirstRunMinutes { get; set; }
    /// <summary>Sample players who finished the story.</summary>
    public int Runs { get; set; }
    public Dictionary<string, RouteItem> Items { get; set; } = new();
    /// <summary>Pairs of achievements players almost never get in one run.</summary>
    public List<string[]> Excludes { get; set; } = new();
}

public sealed class RouteItem
{
    /// <summary>Share of the first runs that gave it.</summary>
    public double FirstRun { get; set; }
    /// <summary>Median place in the first runs that gave it: 0 the start, 1 the end. null - none gave it.</summary>
    public double? Position { get; set; }
    /// <summary>Median place among the unlocks after the first run: 0 right after it, 1 the last. null - none came after.</summary>
    public double? After { get; set; }
}

/// <summary>Where an achievement stands on the player's way.</summary>
public enum RoutePart
{
    /// <summary>The game has no route model.</summary>
    None = 0,
    /// <summary>Comes in the run being played.</summary>
    ThisRun = 1,
    /// <summary>Not in this run: passed by, or another outcome is taken; it takes a replay.</summary>
    NextRun = 2,
    /// <summary>Not tied to the story: players get it after the first run.</summary>
    AfterStory = 3,
}

/// <param name="Minutes">Minutes of a typical player from the start of the game to each achievement on this route.</param>
/// <param name="Parts">Where each achievement stands (<see cref="RoutePart"/>).</param>
/// <param name="TakenInstead">Locked ones another achievement of this run excludes: that one.</param>
/// <param name="Passed">Locked story ones this run went past without them.</param>
public sealed record RoutePlan(
    IReadOnlyDictionary<string, double> Minutes,
    IReadOnlyDictionary<string, RoutePart> Parts,
    IReadOnlyDictionary<string, string> TakenInstead,
    IReadOnlySet<string> Passed,
    bool Finished);

public static class RouteModel
{
    /// <summary>Players who finished the story needed to trust the model.</summary>
    public const int MinRuns = 12;

    /// <summary>An achievement that most first runs give is expected in this one.</summary>
    private const double Majority = 0.5;

    /// <summary>A story achievement: at least this share of the first runs give it. Less - it is after the story.</summary>
    private const double StoryShare = 0.05;

    /// <summary>How far before the player's place a story achievement has to be to count as passed: places are medians.</summary>
    private const double Slack = 0.05;

    /// <summary>Achievements players get in one run at most this share of what chance gives exclude each other.</summary>
    private const double ExcludeRatio = 0.5;

    /// <summary>
    /// Builds the model from the sampled players' minutes of play at each unlock. null - no achievement finishes
    /// the story, or too few players finished it.
    /// </summary>
    public static RouteData? Build(GameData game, IReadOnlyList<IReadOnlyDictionary<string, double>> players)
    {
        var finales = game.Achievements.Where(a => KindClassifier.IsFinale(a.Name, a.Description, game.Name))
            .Select(a => a.ApiName).ToHashSet(StringComparer.Ordinal);
        if (finales.Count == 0) return null;

        var runs = new List<(Dictionary<string, double> First, Dictionary<string, double> Later, double End)>();
        foreach (var played in players)
        {
            var ends = played.Where(kv => finales.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            if (ends.Count == 0) continue;
            double end = ends.Min();
            if (end <= 0) continue;
            var first = played.Where(kv => kv.Value <= end).ToDictionary(kv => kv.Key, kv => kv.Value / end, StringComparer.Ordinal);
            var later = played.Where(kv => kv.Value > end).ToList();
            double span = later.Count == 0 ? 0 : later.Max(kv => kv.Value) - end;
            runs.Add((first, later.ToDictionary(kv => kv.Key, kv => span > 0 ? (kv.Value - end) / span : 1, StringComparer.Ordinal), end));
        }
        if (runs.Count < MinRuns) return null;

        var data = new RouteData
        {
            Finales = game.Achievements.Select(a => a.ApiName).Where(finales.Contains).ToList(),
            FirstRunMinutes = Math.Round(Median(runs.Select(r => r.End)), 0),
            Runs = runs.Count,
        };
        foreach (var def in game.Achievements)
        {
            var name = def.ApiName;
            var at = runs.Where(r => r.First.ContainsKey(name)).Select(r => r.First[name]).ToList();
            var after = runs.Where(r => r.Later.ContainsKey(name)).Select(r => r.Later[name]).ToList();
            if (at.Count == 0 && after.Count == 0) continue;
            data.Items[name] = new RouteItem
            {
                FirstRun = Math.Round((double)at.Count / runs.Count, 3),
                Position = at.Count > 0 ? Math.Round(Median(at), 3) : null,
                After = after.Count > 0 ? Math.Round(Median(after), 3) : null,
            };
        }

        var story = data.Items.Where(kv => kv.Value.FirstRun is >= 0.03 and <= 0.97).Select(kv => kv.Key).ToList();
        for (int i = 0; i < story.Count; i++)
        {
            for (int j = i + 1; j < story.Count; j++)
            {
                string a = story[i], b = story[j];
                double expected = data.Items[a].FirstRun * data.Items[b].FirstRun * runs.Count;
                if (expected < 2.5) continue;
                int both = runs.Count(r => r.First.ContainsKey(a) && r.First.ContainsKey(b));
                if (both <= ExcludeRatio * expected) data.Excludes.Add([a, b]);
            }
        }
        return data;
    }

    /// <summary>
    /// The route of the run being played: the achievements done are in it, story ones well before the furthest of them
    /// are passed, and of the ones ahead each is in when most first runs gave it and nothing in the run excludes it,
    /// nor does a likelier one ahead. In the run an achievement comes at its place in the first run; out of it, after
    /// the first run, spread over the rest of the time to 100%. null - the game has no model.
    /// </summary>
    /// <param name="notYet">Achievements just unlocked in Steam, taken as before that: done only by a manual mark.</param>
    public static RoutePlan? PlanFor(GameData game, IReadOnlySet<string>? notYet = null)
    {
        if (game.Route is not { } route || route.Items.Count == 0) return null;
        bool Done(string name) => GameLogic.IsDone(game, name, notYet);

        double firstRun = route.FirstRunMinutes;
        double total = game.UsedCompletionMinutes is { } t && t > firstRun * 1.1 ? t : firstRun * 2.5;
        bool finished = route.Finales.Any(Done);

        var excludes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in route.Excludes.Where(p => p.Length == 2))
        {
            (excludes.TryGetValue(pair[0], out var x) ? x : excludes[pair[0]] = []).Add(pair[1]);
            (excludes.TryGetValue(pair[1], out var y) ? y : excludes[pair[1]] = []).Add(pair[0]);
        }

        bool IsStory(string name) => route.Items.TryGetValue(name, out var it) && it.Position is not null && it.FirstRun >= StoryShare;
        double reached = finished ? 1 : game.Achievements.Select(a => a.ApiName)
            .Where(n => Done(n) && IsStory(n)).Select(n => route.Items[n].Position!.Value).DefaultIfEmpty(0).Max();

        var inRun = new Dictionary<string, bool>(StringComparer.Ordinal);
        var takenInstead = new Dictionary<string, string>(StringComparer.Ordinal);
        var passed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in route.Items.Keys.Where(Done)) inRun[name] = true;
        foreach (var (name, item) in route.Items.Where(kv => !Done(kv.Key) && IsStory(kv.Key)).OrderBy(kv => kv.Value.Position))
        {
            var rivals = excludes.GetValueOrDefault(name) ?? [];
            if (rivals.FirstOrDefault(r => inRun.GetValueOrDefault(r)) is { } taken)
            {
                inRun[name] = false;
                if (Done(taken)) takenInstead[name] = taken;
                continue;
            }
            if (finished || item.Position < reached - Slack)
            {
                inRun[name] = false;
                passed.Add(name);
                continue;
            }
            inRun[name] = item.FirstRun >= Majority &&
                !rivals.Any(r => !inRun.ContainsKey(r) && !Done(r) && route.Items[r].FirstRun > item.FirstRun);
        }

        var minutes = new Dictionary<string, double>(StringComparer.Ordinal);
        var parts = new Dictionary<string, RoutePart>(StringComparer.Ordinal);
        foreach (var def in game.Achievements)
        {
            var name = def.ApiName;
            if (!route.Items.TryGetValue(name, out var item))
            {
                minutes[name] = total;
                parts[name] = RoutePart.AfterStory;
                continue;
            }
            bool story = IsStory(name);
            if (inRun.GetValueOrDefault(name) && item.Position is { } position)
            {
                minutes[name] = Math.Round(position * firstRun, 1);
                parts[name] = RoutePart.ThisRun;
            }
            else
            {
                // Nobody got it after the first run: a replay of its part may come any time.
                minutes[name] = Math.Round(firstRun + (item.After ?? (story ? 0.5 : 1)) * (total - firstRun), 1);
                parts[name] = story ? RoutePart.NextRun : RoutePart.AfterStory;
            }
        }
        return new RoutePlan(minutes, parts, takenInstead, passed, finished);
    }

    /// <summary>Some achievement finishes the story: a sample would show the route through it.</summary>
    public static bool HasFinale(GameData game) =>
        game.Achievements.Any(a => KindClassifier.IsFinale(a.Name, a.Description, game.Name));

    /// <summary>The achievements the given one is never had with in one run.</summary>
    public static IReadOnlyList<string> RivalsOf(RouteData route, string apiName) =>
        route.Excludes.Where(p => p.Length == 2 && (p[0] == apiName || p[1] == apiName))
            .Select(p => p[0] == apiName ? p[1] : p[0]).ToList();

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return n == 0 ? 0 : n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
    }
}
