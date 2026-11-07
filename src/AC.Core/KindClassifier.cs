using System.Text.RegularExpressions;

namespace AC.Core;

/// <summary>
/// Rough kind heuristic from the name and description (English and Russian).
/// The goal is a reasonable start; the user fixes the rest by hand.
/// Check order matters: missable beats story, grind beats "other".
/// Verbs are matched in all common forms: Steam uses the imperative, infinitive and past tense.
/// </summary>
public static class KindClassifier
{
    private static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Missable = new(
        @"\bmissable\b|point of no return|before (leaving|you leave|the end|finishing|completing)|" +
        @"without (ever )?(dying|being (seen|detected|hit))|in a single (run|playthrough|life)|" +
        @"\bspare (the|him|her|them|his|their|its|everyone|all)\b|\bsave (him|her|them)\b|" +
        @"можно пропустить|легко пропустить|не пропусти|безвозврат|(до того,? как|прежде,? чем) (вы )?(покин|уйд|уход|законч|заверш|пройд)|ни разу не|не умира|не погиб|" +
        @"за одно прохождение|\bпощад(и|ить|ил|ила|или|ите|ив)\b",
        Opt);

    // A number after "chapter", "act" or "episode" is a place, not a grind counter.
    private static readonly Regex Grind = new(
        @"\b(collect(s|ed|ing)?|find(s|ing)?|found|kill(s|ed|ing)?|defeat(s|ed|ing)?|destroy(s|ed|ing)?|earn(s|ed|ing)?|" +
        @"gather(s|ed|ing)?|craft(s|ed|ing)?|win(s|ning)?|won|complet(e|es|ed|ing)|reach(es|ed|ing)?|get(s|ting)?|got|" +
        @"open(s|ed|ing)?|catch(es|ing)?|caught)\b[^.]{0,40}" +
        @"\b((?<!\b(chapter|act|episode)\s+)\d{2,}[\d,\s]*|all|every)\b|" +
        @"\b\d{3,}\b|\ball (the )?(collectibles|items|weapons|cards|trophies|achievements|secrets|upgrades)\b|" +
        @"\bmax(imum|ed)? level\b|\blevel \d{2,}\b|" +
        @"(собер|собра|найд|найти|наш[её]л|нашли|уби|убе|уничтож|заработ|победи|выигра|откр|дости[гч]|получ|выполн|накоп)[^.]{0,40}" +
        @"((?<!\b(глав|акт|эпизод)[а-я]*\s+)\b\d{2,}|\bвс[её]х?\b|\bкажд)|" +
        @"максимальн[а-я]* уров|уровн[яе] \d{2,}",
        Opt);

    // Only "complete/finish a story part" and words that mean story by themselves.
    // A mere chapter mention ("heart in chapter 3") is a place: it sets the group, not the kind.
    private static readonly Regex Story = new(
        @"\b(complet(e|es|ed|ing)|finish(es|ed|ing)?|beat(s|en|ing)?|clear(s|ed|ing)?)\b[^.]{0,30}" +
        @"\b(chapter|act|prologue|epilogue|mission|the game|the story|campaign|level \d|episode|part)\b|" +
        @"\bstory\b|\bending\b|\bfinal boss\b|\bprologue\b|\bepilogue\b|" +
        @"(пройд|пройти|прош[её]л|прошли|заверш|законч)[^.]{0,30}(глав|акт|пролог|эпилог|мисси|игру|сюжет|кампани|эпизод)|" +
        @"пролог|эпилог|концовк|финальн[а-я]* босс|\bсюжет",
        Opt);

    // The end of the whole story, not of a chapter: "finish the game", "see the credits", an ending.
    private static readonly Regex Finale = new(
        @"\b(finish(es|ed|ing)?|complet(e|es|ed|ing)|beat(s|en|ing)?|clear(s|ed|ing)?)\b[^.]{0,25}\b(the game|the story|the main story|the campaign|story mode)\b|" +
        @"\b(see|saw|seen|watch(es|ed)?|roll(s|ed)?)\b[^.]{0,15}\bcredits\b|(?<![-\w])ending\b|" +
        @"(пройд|пройти|прош[её]л|прошли|прошла|заверш|законч)[а-яё]*[^.]{0,25}\b(игру|сюжет|историю|кампани)|\bтитры\b|\bконцовк",
        Opt);

    // "in chapter 3", "Chapter IV", "act 2" - for the automatic "one sitting" group.
    private static readonly Regex Place = new(
        @"\b(?<kind>chapter|act|episode)\s+(?<n>\d{1,3}|[ivx]{1,5})\b|" +
        @"\b(?<kind>глав(а|ы|е|у|ой|ою)|акт(а|е|у|ом)?|эпизод(а|е|у|ом)?)\s+(?<n>\d{1,3}|[ivx]{1,5})\b",
        Opt);

    public static AchievementKind Classify(string? name, string? description)
    {
        var text = $"{name} {description}".Trim();
        if (text.Length == 0) return AchievementKind.Unknown;
        if (Missable.IsMatch(text)) return AchievementKind.Missable;
        if (Story.IsMatch(text) && !Grind.IsMatch(text)) return AchievementKind.Story;
        if (Grind.IsMatch(text)) return AchievementKind.Grind;
        if (Story.IsMatch(text)) return AchievementKind.Story;
        return AchievementKind.Other;
    }

    /// <summary>The achievement finishes the story: the game, the story or the campaign, its credits or an ending.</summary>
    /// <param name="game">
    /// The game's name: "Finish HEAVY RAIN" names the game instead of saying "the game". Only with nothing after it:
    /// "beat Celeste on PICO-8" is a game inside the game.
    /// </param>
    public static bool IsFinale(string? name, string? description, string? game)
    {
        if (Finale.IsMatch($"{name} {description}")) return true;
        if (string.IsNullOrWhiteSpace(game)) return false;
        var title = Regex.Escape(game.Trim());
        var pattern = $@"\b(finish|complet|beat|clear)\w*\s+(the\s+)?{title}[\s\p{{P}}\p{{S}}]*$|(пройд|пройти|прош|заверш|законч)[а-яё]*\s+{title}[\s\p{{P}}\p{{S}}]*$";
        return Regex.IsMatch(name ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
               Regex.IsMatch(description ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Where the achievement is earned: "Chapter 3", "Act 2", "Episode 1", "Episode 2, chapter 3" or null.
    /// The description is checked before the name: it is more precise.
    /// </summary>
    public static string? Location(string? name, string? description)
    {
        foreach (var text in new[] { description, name })
            if (Find(text) is { } place) return place.Label;
        return null;
    }

    /// <summary>The part of the game a text names, as a series ("chapter", "act", "episode chapter"...) and a number.</summary>
    public static (string Series, int Number)? PlaceIn(string? text) => Find(text) is { } place ? (place.Series, place.Number) : null;

    /// <summary>
    /// The part of the game a text names. A chapter of an episode or act ("Chapter 2 of Episode 3") is a series of its
    /// own, numbered within the whole: chapters of different episodes are not one series.
    /// </summary>
    private static (string Series, int Number, string Label)? Find(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = Place.Matches(text).Select(m => (Series: Series(m), N: m.Groups["n"].Value.ToUpperInvariant())).ToList();
        if (parts.Count == 0) return null;
        var chapter = parts.FirstOrDefault(p => p.Series == "chapter");
        var whole = parts.FirstOrDefault(p => p.Series != "chapter");
        if (chapter.N is not null && whole.N is not null)
            return (whole.Series + " chapter", Number(whole.N) * 1000 + Number(chapter.N),
                Loc.T(whole.Series == "episode" ? "group.episodeChapter" : "group.actChapter", whole.N, chapter.N));
        var (series, n) = parts[0];
        return (series, Number(n), Loc.T(series switch { "chapter" => "group.chapter", "episode" => "group.episode", _ => "group.act" }, n));
    }

    private static int Number(string n) => int.TryParse(n, out var arabic) ? arabic : Roman(n);

    private static string Series(Match m)
    {
        var kind = m.Groups["kind"].Value.ToLowerInvariant();
        return kind.StartsWith("ch") || kind.StartsWith("глав") ? "chapter"
            : kind.StartsWith("ep") || kind.StartsWith("эпизод") ? "episode"
            : "act";
    }

    private static int Roman(string s)
    {
        int total = 0, previous = 0;
        foreach (var c in s.ToLowerInvariant().Reverse())
        {
            int v = c switch { 'i' => 1, 'v' => 5, _ => 10 };
            total += v < previous ? -v : v;
            previous = Math.Max(previous, v);
        }
        return total;
    }
}
