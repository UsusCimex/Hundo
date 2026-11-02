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

    /// <summary>
    /// Where the achievement is earned: "Chapter 3", "Act 2", "Episode 1" or null.
    /// The description is checked before the name: it is more precise.
    /// </summary>
    public static string? Location(string? name, string? description)
    {
        foreach (var text in new[] { description, name })
        {
            if (string.IsNullOrEmpty(text)) continue;
            var m = Place.Match(text);
            if (!m.Success) continue;
            var label = Series(m) switch { "chapter" => "group.chapter", "episode" => "group.episode", _ => "group.act" };
            return Loc.T(label, m.Groups["n"].Value.ToUpperInvariant());
        }
        return null;
    }

    /// <summary>The part of the game a text names, as a series ("chapter", "act", "episode") and a number.</summary>
    public static (string Series, int Number)? PlaceIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = Place.Match(text);
        if (!m.Success) return null;
        var n = m.Groups["n"].Value;
        return (Series(m), int.TryParse(n, out var arabic) ? arabic : Roman(n));
    }

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
