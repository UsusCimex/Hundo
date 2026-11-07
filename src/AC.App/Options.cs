using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>A drop-down item: a value and a caption by translation key; the caption follows the language.</summary>
public sealed class Option<T> : ObservableObject
{
    private readonly string? _key;
    private readonly string _title;

    public Option(T value, string key) : this(value, key, "") { }

    private Option(T value, string? key, string title)
    {
        Value = value;
        _key = key;
        _title = title;
        // Options live in static lists for the whole run, so the subscription never has to end.
        if (key is not null) Loc.Changed += () => OnPropertyChanged(nameof(Title));
    }

    /// <summary>Untranslated caption: each language name is in that language.</summary>
    public static Option<T> Fixed(T value, string title) => new(value, null, title);

    public T Value { get; }
    public string Title => _key is null ? _title : Loc.T(_key);
    public override string ToString() => Title;
}

public static class Options
{
    public static IReadOnlyList<Option<AchievementKind>> Kinds { get; } = new[]
    {
        Option<AchievementKind>.Fixed(AchievementKind.Unknown, "-"),
        new Option<AchievementKind>(AchievementKind.Story, "kind.story"),
        new Option<AchievementKind>(AchievementKind.Missable, "kind.missable"),
        new Option<AchievementKind>(AchievementKind.Grind, "kind.grind"),
        new Option<AchievementKind>(AchievementKind.Other, "kind.other"),
    };

    /// <summary>Filter by type: null - all.</summary>
    public static IReadOnlyList<Option<AchievementKind?>> KindFilters { get; } = new[]
    {
        new Option<AchievementKind?>(null, "kind.all"),
        new Option<AchievementKind?>(AchievementKind.Story, "kind.story"),
        new Option<AchievementKind?>(AchievementKind.Missable, "kind.missable"),
        new Option<AchievementKind?>(AchievementKind.Grind, "kind.grind"),
        new Option<AchievementKind?>(AchievementKind.Other, "kind.other"),
        new Option<AchievementKind?>(AchievementKind.Unknown, "kind.none"),
    };

    public static IReadOnlyList<Option<SortMode>> Sorts { get; } = new[]
    {
        new Option<SortMode>(SortMode.Plan, "sort.plan"),
        new Option<SortMode>(SortMode.Time, "sort.time"),
        new Option<SortMode>(SortMode.Rarity, "sort.rarity"),
        new Option<SortMode>(SortMode.Name, "sort.name"),
        new Option<SortMode>(SortMode.UnlockTime, "sort.unlockTime"),
    };

    public static IReadOnlyList<Option<Percentile>> Percentiles { get; } = new[]
    {
        new Option<Percentile>(Percentile.P50, "percentile.p50"),
        new Option<Percentile>(Percentile.P75, "percentile.p75"),
        new Option<Percentile>(Percentile.P90, "percentile.p90"),
    };

    public static IReadOnlyList<Option<LibrarySort>> LibrarySorts { get; } = new[]
    {
        new Option<LibrarySort>(LibrarySort.Recent, "library.sortRecent"),
        new Option<LibrarySort>(LibrarySort.TimeLeft, "library.sortLeft"),
    };

    public static IReadOnlyList<Option<HoursMode>> HourModes { get; } = new[]
    {
        new Option<HoursMode>(HoursMode.FromStart, "hours.fromStart"),
        new Option<HoursMode>(HoursMode.Left, "hours.left"),
    };

    public static IReadOnlyList<Option<AppTheme>> Themes { get; } = new[]
    {
        new Option<AppTheme>(AppTheme.System, "option.system"),
        new Option<AppTheme>(AppTheme.Light, "theme.light"),
        new Option<AppTheme>(AppTheme.Dark, "theme.dark"),
    };

    public static IReadOnlyList<Option<WriteMode>> WriteModes { get; } = new[]
    {
        new Option<WriteMode>(WriteMode.MarkOnly, "writeMode.markOnly"),
        new Option<WriteMode>(WriteMode.MarkAndUnlock, "writeMode.markAndUnlock"),
        new Option<WriteMode>(WriteMode.Queue, "writeMode.queue"),
    };

    public static IReadOnlyList<Option<StatusSource>> StatusSources { get; } = new[]
    {
        new Option<StatusSource>(StatusSource.WebApi, "source.webApi"),
        new Option<StatusSource>(StatusSource.SteamClient, "source.client"),
    };

    /// <summary>Interface languages: as in Windows, then every translation under its own name.</summary>
    public static IReadOnlyList<Option<string>> UiLanguages { get; } =
        [new Option<string>("", "option.system"), .. Loc.Languages.Select(l => Option<string>.Fixed(l, Loc.NativeName(l)))];

    /// <summary>Steam languages for achievement names and descriptions; empty means the interface language.</summary>
    public static IReadOnlyList<Option<string>> ContentLanguages { get; } =
    [
        new Option<string>("", "settings.languageAuto"),
        Option<string>.Fixed("english", "English"),
        Option<string>.Fixed("russian", "Русский"),
        Option<string>.Fixed("ukrainian", "Українська"),
        Option<string>.Fixed("german", "Deutsch"),
        Option<string>.Fixed("french", "Français"),
        Option<string>.Fixed("spanish", "Español"),
        Option<string>.Fixed("brazilian", "Português (Brasil)"),
        Option<string>.Fixed("italian", "Italiano"),
        Option<string>.Fixed("polish", "Polski"),
        Option<string>.Fixed("turkish", "Türkçe"),
        Option<string>.Fixed("japanese", "日本語"),
        Option<string>.Fixed("koreana", "한국어"),
        Option<string>.Fixed("schinese", "简体中文"),
        Option<string>.Fixed("tchinese", "繁體中文"),
    ];

    public static string KindTitle(AchievementKind k) => Kinds.FirstOrDefault(o => o.Value == k)?.Title ?? "-";

    public static string StageTitle(PlanStage s) => s switch
    {
        PlanStage.Missable => Loc.T("stage.missable"),
        PlanStage.Story => Loc.T("stage.story"),
        PlanStage.Other => Loc.T("stage.other"),
        PlanStage.Grind => Loc.T("stage.grind"),
        PlanStage.ThisRun => Loc.T("stage.thisRun"),
        PlanStage.NextRun => Loc.T("stage.nextRun"),
        PlanStage.AfterStory => Loc.T("stage.afterStory"),
        _ => s.ToString(),
    };
}
