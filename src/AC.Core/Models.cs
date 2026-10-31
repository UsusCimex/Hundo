using System.Text.Json.Serialization;

namespace AC.Core;

/// <summary>Achievement kind. Steam only knows the hidden flag; the rest is a heuristic or manual tags.</summary>
public enum AchievementKind
{
    Unknown = 0,
    Story = 1,
    Grind = 2,
    Missable = 3,
    Other = 4,
}

public enum KindSource
{
    Auto = 0,
    Manual = 1,
}

/// <summary>The resulting status the user sees.</summary>
public enum EffectiveStatus
{
    Locked = 0,
    Unlocked = 1,
    /// <summary>Done, but not counted by Steam (manual mark).</summary>
    ManualDone = 2,
}

/// <summary>Achievement definition from the game schema.</summary>
public sealed class AchievementDef
{
    public string ApiName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Hidden { get; set; }
    public string IconUrl { get; set; } = "";
    public string IconGrayUrl { get; set; } = "";
    /// <summary>Global unlock percentage from Steam, 0..100. null - Steam did not provide it.</summary>
    public double? GlobalPercent { get; set; }
    /// <summary>Percentage in our own player sample (when Steam does not provide global stats).</summary>
    public double? SamplePercent { get; set; }

    /// <summary>The percentage shown and used in estimates: from Steam, else from the sample.</summary>
    [JsonIgnore]
    public double? Percent => GlobalPercent ?? SamplePercent;

    [JsonIgnore]
    public bool PercentFromSample => GlobalPercent is null && SamplePercent is not null;
    /// <summary>Protected: set only by the game server, cannot be written through the Steam client.</summary>
    public bool Protected { get; set; }
}

/// <summary>The player's state of an achievement.</summary>
public sealed class AchievementState
{
    public bool SteamUnlocked { get; set; }
    public DateTimeOffset? SteamUnlockTime { get; set; }
    public bool ManualDone { get; set; }
    public DateTimeOffset? ManualAt { get; set; }
    /// <summary>When the app marked it by time (auto mark): one the user unmarks is not marked by time again.</summary>
    public DateTimeOffset? AutoMarkedAt { get; set; }
    public string Note { get; set; } = "";
    /// <summary>The user took it off the overlay: a missed one stays there until the next playthrough otherwise.</summary>
    public bool OffOverlay { get; set; }
}

/// <summary>Achievement tags: kind and "one sitting" group.</summary>
public sealed class AchievementTag
{
    public AchievementKind Kind { get; set; }
    public KindSource Source { get; set; }
    /// <summary>Group label: chapter, location, mode. Empty - no group.</summary>
    public string Group { get; set; } = "";
    /// <summary>
    /// Group origin: Auto - from the description ("in chapter 3"), Manual - set by the user.
    /// null - not recorded (old file format, where groups were only set manually).
    /// </summary>
    public KindSource? GroupSource { get; set; }

    /// <summary>The group was set by the user (in old files - any non-empty one).</summary>
    [JsonIgnore]
    public bool GroupIsManual => (GroupSource ?? (Group.Length > 0 ? KindSource.Manual : KindSource.Auto)) == KindSource.Manual;
}

/// <summary>Steam playtime of a game at a moment.</summary>
public sealed class PlaytimePoint
{
    public DateTimeOffset At { get; set; }
    public double Minutes { get; set; }
}

/// <summary>Time estimate "from the start of the game" in minutes.</summary>
public sealed class TimeEstimate
{
    public string Source { get; set; } = "";
    public double? P50 { get; set; }
    public double? P75 { get; set; }
    public double? P90 { get; set; }
    public int SampleSize { get; set; }

    public double? Get(Percentile p) => p switch
    {
        Percentile.P75 => P75 ?? P50,
        Percentile.P90 => P90 ?? P75 ?? P50,
        _ => P50,
    };
}

public enum Percentile
{
    P50 = 50,
    P75 = 75,
    P90 = 90,
}

/// <summary>Everything stored for one game.</summary>
public sealed class GameData
{
    public uint AppId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Playtime according to Steam.</summary>
    public int PlaytimeMinutes { get; set; }
    /// <summary>When PlaytimeMinutes was read from Steam: mid-session it counts the session up to about then. null - unknown.</summary>
    public DateTimeOffset? PlaytimeReadAt { get; set; }
    /// <summary>
    /// The user's own "actually played" minutes. When set, replaces the Steam figure
    /// (for example, lots of AFK or part of the game played in another version).
    /// </summary>
    public double? PlaytimeOverrideMinutes { get; set; }
    /// <summary>When the own minutes were set: the play seen after that goes on top of them.</summary>
    public DateTimeOffset? PlaytimeOverrideAt { get; set; }
    /// <summary>
    /// Minutes of Steam playtime to ignore: hours before the game got achievements,
    /// or AFK. Applies only when PlaytimeOverrideMinutes is not set.
    /// </summary>
    public double PlaytimeOffsetMinutes { get; set; }

    /// <summary>Playtime used to measure progress toward 100%.</summary>
    [JsonIgnore]
    public double EffectivePlaytimeMinutes =>
        PlaytimeOverrideMinutes ?? Math.Max(0, PlaytimeMinutes - PlaytimeOffsetMinutes);

    /// <summary>Steam playtime seen between sessions, oldest first: tells how much was played between unlocks.</summary>
    public List<PlaytimePoint> PlaytimeHistory { get; set; } = new();
    /// <summary>Time to 100% for the game in minutes (median from an external site, or manual).</summary>
    public double? CompletionMinutes { get; set; }
    public DateTimeOffset? SchemaFetchedAt { get; set; }
    public DateTimeOffset? StateFetchedAt { get; set; }
    public string SchemaLanguage { get; set; } = "";

    public List<AchievementDef> Achievements { get; set; } = new();
    public Dictionary<string, AchievementState> States { get; set; } = new();
    public Dictionary<string, AchievementTag> Tags { get; set; } = new();
    /// <summary>Automatic time estimates.</summary>
    public Dictionary<string, TimeEstimate> Estimates { get; set; } = new();
    /// <summary>Manual time estimates (minutes), preferred over automatic ones.</summary>
    public Dictionary<string, double> ManualEstimates { get; set; } = new();
    /// <summary>Marked and waiting to be sent to Steam (the "queue" write mode).</summary>
    public List<string> PendingUnlocks { get; set; } = new();
    /// <summary>Where the time to 100% came from: "manual", "steamhunters.com".</summary>
    public string CompletionSource { get; set; } = "";
    /// <summary>When the time to 100% was last asked from Steam Hunters, found or not.</summary>
    public DateTimeOffset? CompletionFetchedAt { get; set; }

    /// <summary>
    /// Time to 100% derived from the player's own progress when CompletionMinutes is not set.
    /// Recomputed on every estimate, not saved.
    /// </summary>
    [JsonIgnore]
    public double? DerivedCompletionMinutes { get; set; }

    /// <summary>
    /// Whether manual "done but not counted" marks count as earned.
    /// Set by the app from its settings, not stored in the game file.
    /// </summary>
    [JsonIgnore]
    public bool CountManualMarks { get; set; } = true;

    /// <summary>Time to 100% the estimates actually use: set or derived.</summary>
    [JsonIgnore]
    public double? UsedCompletionMinutes =>
        CompletionMinutes is > 0 ? CompletionMinutes :
        SampleCompletionMinutes is > 0 ? SampleCompletionMinutes :
        DerivedCompletionMinutes;

    /// <summary>Median time to 100% in the player sample.</summary>
    public double? SampleCompletionMinutes { get; set; }
    /// <summary>Players in the sample (with a public profile).</summary>
    public int SamplePlayers { get; set; }
    /// <summary>When a sample was last collected on its own, successful or not: it is not retried for a week.</summary>
    public DateTimeOffset? SampleTriedAt { get; set; }
    /// <summary>How many of them reached 100%.</summary>
    public int SampleCompleters { get; set; }
    public DateTimeOffset? SampleFetchedAt { get; set; }

    public AchievementState StateOf(string apiName)
    {
        if (!States.TryGetValue(apiName, out var s))
        {
            s = new AchievementState();
            States[apiName] = s;
        }
        return s;
    }

    public AchievementTag TagOf(string apiName)
    {
        if (!Tags.TryGetValue(apiName, out var t))
        {
            t = new AchievementTag();
            Tags[apiName] = t;
        }
        return t;
    }
}

/// <summary>List row: schema + state + tags + estimate.</summary>
/// <param name="Behind">Locked, and the player has gone past it by the order of unlocks (<see cref="GameLogic.Behind"/>).</param>
/// <param name="OffOverlay">The user took it off the overlay (<see cref="AchievementState.OffOverlay"/>).</param>
/// <param name="AutoMarkUndone">Locked, though the app once marked it by time: the user took that mark back.</param>
public sealed record AchievementRow(
    AchievementDef Def,
    EffectiveStatus Status,
    AchievementKind Kind,
    string Group,
    double? EstimateMinutes,
    bool EstimateIsManual,
    string Note,
    DateTimeOffset? UnlockTime,
    bool GroupIsAuto = false,
    bool ProbablyMissed = false,
    bool Behind = false,
    bool OffOverlay = false,
    bool AutoMarkUndone = false)
{
    public string ApiName => Def.ApiName;
    public bool IsDone => Status != EffectiveStatus.Locked;

    /// <summary>Not on the way to the next ones: gone past, or probably missed.</summary>
    public bool Passed => !IsDone && (Behind || ProbablyMissed);
}
