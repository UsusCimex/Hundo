namespace AC.Core;

/// <summary>Combines statuses and builds the list rows.</summary>
public static class GameLogic
{
    public static EffectiveStatus StatusOf(AchievementState? s, bool manualMarks = true)
    {
        if (s is null) return EffectiveStatus.Locked;
        if (s.SteamUnlocked) return EffectiveStatus.Unlocked;
        if (manualMarks && s.ManualDone) return EffectiveStatus.ManualDone;
        return EffectiveStatus.Locked;
    }

    /// <summary>How long the Web API may take to show what the app wrote to Steam: past that, it is taken as it is.</summary>
    public static readonly TimeSpan WriteShownWithin = TimeSpan.FromDays(1);

    /// <summary>
    /// Applies fresh status from Steam. Manual marks and notes are kept:
    /// once the achievement unlocks in Steam, its status becomes "unlocked" by itself.
    /// Returns the api names unlocked since the last sync; none at the first one, which finds what came before.
    /// </summary>
    /// <param name="lagging">
    /// The status comes from the Web API, which shows a write a while after the Steam client takes it: until it shows
    /// what the app wrote (<see cref="AchievementState.WrittenAt"/>), the write stands.
    /// </param>
    public static IReadOnlyList<string> ApplySteamState(
        GameData game,
        IEnumerable<(string ApiName, bool Unlocked, DateTimeOffset? UnlockTime)> steam,
        DateTimeOffset now,
        bool lagging = false)
    {
        bool first = game.StateFetchedAt is null;
        var newlyUnlocked = new List<string>();
        foreach (var (apiName, unlocked, time) in steam)
        {
            var s = game.StateOf(apiName);
            if (lagging && s.WrittenAt is { } written)
            {
                if (unlocked != s.SteamUnlocked && now - written < WriteShownWithin) continue;
                s.WrittenAt = null;
            }
            if (unlocked && !s.SteamUnlocked && !first) newlyUnlocked.Add(apiName);
            s.SteamUnlocked = unlocked;
            s.SteamUnlockTime = unlocked ? time : null;
        }
        game.StateFetchedAt = now;
        return newlyUnlocked;
    }

    public static double? EstimateOf(GameData game, string apiName, Percentile p, out bool manual)
    {
        if (game.ManualEstimates.TryGetValue(apiName, out var m))
        {
            manual = true;
            return m;
        }
        manual = false;
        return game.Estimates.TryGetValue(apiName, out var e) ? e.Get(p) : null;
    }

    public static IReadOnlyList<AchievementRow> BuildRows(GameData game, Percentile p)
    {
        var behind = Behind(game);
        var missed = Missed(game, behind);
        var rows = new List<AchievementRow>(game.Achievements.Count);
        foreach (var def in game.Achievements)
        {
            game.States.TryGetValue(def.ApiName, out var s);
            game.Tags.TryGetValue(def.ApiName, out var t);
            var est = EstimateOf(game, def.ApiName, p, out var manual);
            var status = StatusOf(s, game.CountManualMarks);
            var kind = t?.Kind ?? AchievementKind.Unknown;
            bool gonePast = behind.Contains(def.ApiName);
            rows.Add(new AchievementRow(
                def,
                status,
                kind,
                t?.Group ?? "",
                est,
                manual,
                s?.Note ?? "",
                status switch
                {
                    EffectiveStatus.Unlocked => s!.SteamUnlockTime,
                    EffectiveStatus.ManualDone => s!.ManualAt,
                    _ => null,
                },
                t is { Group.Length: > 0, GroupIsManual: false },
                missed.Contains(def.ApiName),
                gonePast,
                s?.OffOverlay == true,
                status == EffectiveStatus.Locked && s?.AutoMarkedAt is not null,
                status == EffectiveStatus.Locked && game.StatProgress.TryGetValue(def.ApiName, out var progress) ? progress : null));
        }
        return rows;
    }

    /// <summary>
    /// The missables the new unlocks left behind: taken for missed now (<see cref="Missed"/>) and not without them,
    /// in the schema's order.
    /// </summary>
    public static IReadOnlyList<string> LeftBehind(GameData game, IReadOnlyCollection<string> unlocked)
    {
        var notYet = unlocked.ToHashSet(StringComparer.Ordinal);
        var before = Missed(game, Behind(game, notYet), notYet);
        var now = Missed(game, Behind(game));
        return game.Achievements.Select(a => a.ApiName).Where(n => now.Contains(n) && !before.Contains(n)).ToList();
    }

    /// <summary>Locked missables taken for missed: their part of the game is over, or the player has gone past them.</summary>
    /// <param name="behind">The achievements gone past (<see cref="Behind"/>).</param>
    private static HashSet<string> Missed(GameData game, IReadOnlySet<string> behind, IReadOnlySet<string>? notYet = null)
    {
        var missed = ProbablyMissed(game, notYet).ToHashSet(StringComparer.Ordinal);
        missed.UnionWith(behind.Where(n => game.Tags.GetValueOrDefault(n)?.Kind == AchievementKind.Missable));
        return missed;
    }

    /// <param name="notYet">Achievements just unlocked in Steam, taken as before that: done only by a manual mark.</param>
    private static bool IsDone(GameData game, string apiName, IReadOnlySet<string>? notYet)
    {
        var s = game.States.GetValueOrDefault(apiName);
        if (notYet?.Contains(apiName) == true) return game.CountManualMarks && s?.ManualDone == true;
        return StatusOf(s, game.CountManualMarks) != EffectiveStatus.Locked;
    }

    /// <summary>
    /// Locked achievements the player has gone past on the typical player's scale (<see cref="Progress.Behind"/>):
    /// players usually unlock them before achievements this player already has.
    /// </summary>
    public static IReadOnlySet<string> Behind(GameData game, IReadOnlySet<string>? notYet = null) =>
        AC.Core.Progress.Behind(game.Achievements
            .Select(a => (a.ApiName, At: EstimateOf(game, a.ApiName, Percentile.P50, out _), Done: IsDone(game, a.ApiName, notYet)))
            .Where(a => a.At.HasValue)
            .Select(a => (a.ApiName, a.At!.Value, a.Done)), Planner.MinMarginMinutes);

    /// <summary>
    /// Locked missable achievements whose part of the game (chapter, act, episode) is behind the player:
    /// a story achievement of that part or a later one is done, or any achievement of a later part.
    /// The place comes from the group, else from the description or the name.
    /// </summary>
    public static IReadOnlySet<string> ProbablyMissed(GameData game, IReadOnlySet<string>? notYet = null)
    {
        var places = new Dictionary<string, (string Series, int Number)>(StringComparer.Ordinal);
        foreach (var a in game.Achievements)
        {
            var group = game.Tags.GetValueOrDefault(a.ApiName)?.Group;
            if ((KindClassifier.PlaceIn(group) ?? KindClassifier.PlaceIn(a.Description) ?? KindClassifier.PlaceIn(a.Name)) is { } place)
                places[a.ApiName] = place;
        }

        // The last part known to be over, per series.
        var over = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, place) in places)
        {
            if (!IsDone(game, name, notYet)) continue;
            int done = game.Tags.GetValueOrDefault(name)?.Kind == AchievementKind.Story ? place.Number : place.Number - 1;
            over[place.Series] = Math.Max(over.GetValueOrDefault(place.Series, int.MinValue), done);
        }

        var missed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, place) in places)
        {
            if (game.Tags.GetValueOrDefault(name)?.Kind != AchievementKind.Missable) continue;
            if (IsDone(game, name, notYet)) continue;
            if (over.TryGetValue(place.Series, out var last) && last >= place.Number) missed.Add(name);
        }
        return missed;
    }

    /// <summary>Progress: (unlocked in Steam, marked manually, total).</summary>
    public static (int Unlocked, int Manual, int Total) Progress(GameData game)
    {
        int u = 0, m = 0;
        foreach (var def in game.Achievements)
        {
            switch (StatusOf(game.States.GetValueOrDefault(def.ApiName), game.CountManualMarks))
            {
                case EffectiveStatus.Unlocked: u++; break;
                case EffectiveStatus.ManualDone: m++; break;
            }
        }
        return (u, m, game.Achievements.Count);
    }

    /// <summary>
    /// Sets the automatic kind and group ("in chapter 3" - group "Chapter 3") wherever they are not set manually.
    /// </summary>
    public static void AutoClassify(GameData game)
    {
        foreach (var def in game.Achievements)
        {
            var tag = game.TagOf(def.ApiName);
            if (tag.Source != KindSource.Manual)
            {
                tag.Kind = KindClassifier.Classify(def.Name, def.Description);
                tag.Source = KindSource.Auto;
            }
            if (!tag.GroupIsManual)
            {
                tag.Group = KindClassifier.Location(def.Name, def.Description) ?? "";
                tag.GroupSource = KindSource.Auto;
            }
        }
    }

    public static void SetKind(GameData game, string apiName, AchievementKind kind)
    {
        var tag = game.TagOf(apiName);
        tag.Kind = kind;
        tag.Source = KindSource.Manual;
    }

    /// <summary>A user-set group. An empty one is manual too: auto-detection will not fill it again.</summary>
    public static void SetGroup(GameData game, string apiName, string group)
    {
        var tag = game.TagOf(apiName);
        tag.Group = group.Trim();
        tag.GroupSource = KindSource.Manual;
    }
}
