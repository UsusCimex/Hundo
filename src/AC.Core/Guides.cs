using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AC.Core;

/// <summary>A guide in a Steam Community list.</summary>
public sealed record GuideCard(ulong Id, string Title, ulong Creator)
{
    /// <summary>The pictures uploaded to the guide: its text refers to them by id.</summary>
    public IReadOnlyList<GuidePicture> Pictures { get; init; } = [];
}

/// <summary>A picture uploaded to a guide.</summary>
public sealed record GuidePicture(string Id, string Url, long Bytes);

/// <summary>A line of a guide section and how it looks: headings, bold and underlined lines start entries.</summary>
/// <param name="Media">Pictures and videos on the line.</param>
/// <param name="Item">The list item the line is in, numbered through the section; 0 - not in a list.</param>
public sealed record GuideLine(string Text, int Heading, double Bold, double Underlined, int Media, int Item)
{
    /// <summary>Addresses of the pictures on the line worth showing: not icons.</summary>
    public IReadOnlyList<string> Pictures { get; init; } = [];
}

public sealed record GuideSection(string Id, string Title, IReadOnlyList<GuideLine> Lines);

public sealed record GuidePage(ulong Id, string Title, string Author, IReadOnlyList<GuideSection> Sections);

/// <summary>An achievement as a guide in some language names it.</summary>
/// <param name="OtherName">Its English name in a guide of another language: authors mix the two.</param>
public sealed record GuideAchievement(string ApiName, string Name, string Description, string OtherName = "");

/// <summary>What a guide says about an achievement.</summary>
/// <param name="Language">Steam language of the guide: english, russian...</param>
/// <param name="Rank">How surely the place was found, 0 - the exact name as a heading; lower is surer.</param>
/// <param name="Text">The guide's own words beyond the name and description; empty when it only shows pictures or a video.</param>
public sealed record GuideTip(ulong GuideId, string GuideTitle, string Author, string Language, string SectionId, int Rank, string Text)
{
    /// <summary>Pictures of the quoted part.</summary>
    public IReadOnlyList<string> Pictures { get; init; } = [];

    [JsonIgnore]
    public string Url => "https://steamcommunity.com/sharedfiles/filedetails/?id=" + GuideId.ToString(CultureInfo.InvariantCulture) +
                         (SectionId.Length > 0 ? "#" + SectionId : "");

    /// <summary>Own words beat pictures, then the surer place, then the longer text.</summary>
    public bool IsBetterThan(GuideTip other) =>
        (Text.Length > 0) != (other.Text.Length > 0) ? Text.Length > 0
        : Rank != other.Rank ? Rank < other.Rank
        : Text.Length > other.Text.Length;
}

/// <summary>Tips for a game's achievements from Steam Community guides, kept on disk.</summary>
public sealed class GuideTips
{
    /// <summary>Tips kept in an older format are read again: before it, tips quoted picture tags as text.</summary>
    public const int CurrentFormat = 2;

    public int Format { get; set; }
    /// <summary>The content language the guides were picked for.</summary>
    public string Language { get; set; } = "";
    public DateTimeOffset FetchedAt { get; set; }
    public Dictionary<string, GuideTip> Tips { get; set; } = new();
    /// <summary>Machine translations of tips: "language:apiName" to the text.</summary>
    public Dictionary<string, string> Translations { get; set; } = new();

    /// <summary>Tips in the content language, English ones where those have no words of their own.</summary>
    public static Dictionary<string, GuideTip> Combine(IReadOnlyDictionary<string, GuideTip> own, IReadOnlyDictionary<string, GuideTip> english)
    {
        var tips = new Dictionary<string, GuideTip>(own, StringComparer.Ordinal);
        foreach (var (api, tip) in english)
            if (!tips.TryGetValue(api, out var mine) || mine.Text.Length == 0 && tip.Text.Length > 0) tips[api] = tip;
        return tips;
    }
}

/// <summary>Steam Community guides: which ones to read and the lines of their sections.</summary>
public static partial class GuideParser
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
        { "*", "list", "olist", "quote", "code", "table", "tr", "hr", "h1", "h2", "h3" };

    /// <summary>Smaller uploads are icons beside the text (items, achievements), not pictures to look at.</summary>
    public const long MinPictureBytes = 8 * 1024;

    /// <summary>
    /// A picture worth showing by its size in pixels: guides put the achievement's icon, even enlarged, and a shot of
    /// its pop-up beside its name, and those tell nothing the app does not show.
    /// </summary>
    public static bool WorthShowing(int width, int height) => Math.Min(width, height) >= 150 && Math.Max(width, height) >= 300;

    /// <summary>
    /// The guides to read first, by their titles, the list's order breaking ties. The list holds guides tagged
    /// Achievements, yet many big ones are about something else; video guides give little to quote.
    /// </summary>
    public static IReadOnlyList<GuideCard> Pick(IReadOnlyList<GuideCard> cards, int max) =>
        cards.Select((card, order) => (Card: card, Score: TitleScore(card.Title), Order: order))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Order)
            .Take(max)
            .Select(x => x.Card)
            .ToList();

    private static int TitleScore(string title)
    {
        var t = title.ToLowerInvariant();
        int score = AchievementWord().IsMatch(t) ? 3 : 1;
        if (t.Contains("100%") || t.Contains("100 %") || t.Contains("complet") || t.Contains("прохожд")) score++;
        if (t.Contains("video") || t.Contains("видео")) score -= 3;
        return score;
    }

    /// <summary>The visual lines of a section's BBCode: a line ends at a line break, a list item, a heading or a block.</summary>
    /// <param name="uploads">The guide's own pictures, for the tags that refer to them by id.</param>
    public static List<GuideLine> Lines(string bbcode, IReadOnlyList<GuidePicture>? uploads = null)
    {
        var uploaded = (uploads ?? []).Where(p => p.Bytes >= MinPictureBytes)
            .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Url, StringComparer.Ordinal);
        var lines = new List<GuideLine>();
        List<(string Text, bool Bold, bool Under)>? segments = null;
        var pictures = new List<string>();
        int media = 0, lineHeading = 0, lineItem = 0, bold = 0, under = 0, heading = 0, item = 0, items = 0;
        var outer = new Stack<int>();

        void Break()
        {
            if (segments is null) return;
            var text = Spaces().Replace(Visible(string.Concat(segments.Select(s => s.Text))), " ").Trim();
            if (text.Length > 0 || media > 0)
            {
                int total = segments.Sum(s => s.Text.Trim().Length);
                double Share(Func<(string Text, bool Bold, bool Under), bool> f) =>
                    total == 0 ? 0 : (double)segments.Where(f).Sum(s => s.Text.Trim().Length) / total;
                pictures.AddRange(Link().Matches(text).Select(l => l.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')')).Where(LinksPicture));
                lines.Add(new GuideLine(text, lineHeading, Share(s => s.Bold), Share(s => s.Under), media, lineItem)
                    { Pictures = pictures.Distinct().ToList() });
            }
            segments = null;
            media = 0;
            pictures.Clear();
        }

        void Start()
        {
            if (segments is not null) return;
            segments = new();
            lineHeading = heading;
            lineItem = item;
        }

        void Text(string text)
        {
            var parts = text.Split('\n');
            for (int p = 0; p < parts.Length; p++)
            {
                if (p > 0) Break();
                if (parts[p].Trim().Length == 0 && segments is null) continue;
                Start();
                segments!.Add((parts[p], bold > 0 || heading > 0, under > 0));
            }
        }

        int i = 0;

        // A picture's own text is a caption, mostly a leftover ("Image"); authors leave the tag unclosed too,
        // then the text after it is the guide's.
        void SkipCaption(string end)
        {
            int next = bbcode.IndexOf('[', i);
            if (next >= 0 && string.Compare(bbcode, next, end, 0, end.Length, StringComparison.OrdinalIgnoreCase) == 0) i = next + end.Length;
        }

        while (i < bbcode.Length)
        {
            int open = bbcode.IndexOf('[', i);
            if (open < 0) open = bbcode.Length;
            if (open > i) Text(bbcode[i..open]);
            if (open >= bbcode.Length) break;
            var m = Tag().Match(bbcode, open);
            var name = m.Success ? m.Groups[2].Value.ToLowerInvariant() : "";
            if (!m.Success || !Known.Contains(name) && !name.StartsWith("preview", StringComparison.Ordinal))
            {
                Text("[");
                i = open + 1;
                continue;
            }
            bool close = m.Groups[1].Length > 0;
            // [previewimg=id;size;file.png], [screenshot=id;size;https://...], [url=https://...]
            var fields = m.Groups[3].Value.Split(';');
            i = open + m.Length;
            if (BlockTags.Contains(name)) Break();
            switch (name)
            {
                case "b":
                    bold = Math.Max(0, bold + (close ? -1 : 1));
                    break;
                case "u":
                    under = Math.Max(0, under + (close ? -1 : 1));
                    break;
                case "h1" or "h2" or "h3":
                    heading = close ? 0 : name[1] - '0';
                    break;
                case "list" or "olist":
                    if (!close) outer.Push(item);
                    else item = outer.Count > 0 ? outer.Pop() : 0;
                    break;
                case "*":
                    item = ++items;
                    break;
                case "td" or "th" when !close:
                    if (segments?.Any(s => s.Text.Trim().Length > 0) == true) Text(" - ");
                    break;
                case "img" when !close:
                    Start();
                    media++;
                    int end = bbcode.IndexOf("[/img]", i, StringComparison.OrdinalIgnoreCase);
                    if (end >= 0 && Shows(bbcode[i..end].Trim())) pictures.Add(bbcode[i..end].Trim());
                    i = end < 0 ? bbcode.Length : end + "[/img]".Length;
                    break;
                case "screenshot" when !close:
                    Start();
                    media++;
                    if (fields.Length >= 3 && Shows(fields[2].Trim())) pictures.Add(fields[2].Trim());
                    SkipCaption("[/screenshot]");
                    break;
                case "url" when !close && LinksPicture(fields[0].Trim()):
                    pictures.Add(fields[0].Trim());
                    break;
                // An icon's text is a label beside it.
                case var preview when !close && preview.StartsWith("preview", StringComparison.Ordinal):
                    Start();
                    media++;
                    if (name != "previewimg") break;
                    if (uploaded.TryGetValue(fields[0].Trim(), out var upload)) pictures.Add(upload);
                    SkipCaption("[/previewimg]");
                    break;
                case "noparse" when !close:
                    int stop = bbcode.IndexOf("[/noparse]", i, StringComparison.OrdinalIgnoreCase);
                    if (stop < 0) stop = bbcode.Length;
                    Text(bbcode[i..stop]);
                    i = Math.Min(bbcode.Length, stop + "[/noparse]".Length);
                    break;
            }
        }
        Break();
        return lines;
    }

    /// <summary>Invisible characters as spaces: authors pad pictures with blank Braille cells and zero-width spaces.</summary>
    private static string Visible(string text) =>
        string.Create(text.Length, text, (span, t) =>
        {
            for (int i = 0; i < t.Length; i++)
                span[i] = t[i] == (char)0x2800 || CharUnicodeInfo.GetUnicodeCategory(t[i]) == UnicodeCategory.Format ? ' ' : t[i];
        });

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "b", "i", "u", "s", "strike", "spoiler", "noparse", "hr", "url", "list", "olist", "*", "quote", "code",
        "table", "tr", "th", "td", "img", "screenshot", "h1", "h2", "h3",
    };

    /// <summary>A picture to show: a web address, but not of Steam's achievement and game icons.</summary>
    private static bool Shows(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        !uri.AbsolutePath.Contains("/public/images/apps/", StringComparison.OrdinalIgnoreCase);

    /// <summary>A link that leads to a picture rather than a page: by the file type or the host.</summary>
    private static bool LinksPicture(string url) =>
        Shows(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (PictureFile().IsMatch(uri.AbsolutePath) || PictureHosts.Contains(uri.Host));

    private static readonly HashSet<string> PictureHosts = new(StringComparer.OrdinalIgnoreCase)
        { "steamuserimages-a.akamaihd.net", "images.steamusercontent.com", "i.imgur.com" };

    [GeneratedRegex(@"\.(?:png|jpe?g|gif|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex PictureFile();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Link();

    [GeneratedRegex(@"\G\[(/?)(\*|[A-Za-z0-9]+)(?:=([^\]\r\n]*))?\]")]
    private static partial Regex Tag();

    [GeneratedRegex(@"achiev|troph|достижен|ачивк")]
    private static partial Regex AchievementWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Finds an achievement's place in a guide and quotes it. A place is a heading or a section title with the
/// achievement's name, exact or after normalizing ("1. Name", "Name - description", near misses), else a line
/// starting with the name, else a line repeating its description. The quote runs to the next heading, the next
/// achievement's entry or the end of its list item, without the name and the description the app already shows.
/// A guide whose title names one achievement is about it as a whole.
/// </summary>
public static partial class GuideMatcher
{
    private const double NearMiss = 0.85;
    private const int MaxLines = 40;
    private const int MaxChars = 1000;

    /// <param name="game">The game's name: a title with it does not single out an achievement of the same name.</param>
    public static Dictionary<string, GuideTip> Match(GuidePage page, string language, IReadOnlyList<GuideAchievement> achievements, string game = "")
    {
        var sections = page.Sections;
        var names = achievements.Select(a => Norm(a.Name)).ToList();
        var starts = achievements.Select(a => StartPattern(a.Name)).ToList();
        // An entry ends where another one starts, by any of its names; places are found by the guide language's names
        // only: the other names turn up in lists that pair them ("Left Behind - Через левое плечо - 1:31").
        var stops = achievements.Select(a => NamesOf(a).Select(StartPattern).OfType<Regex>().ToList()).ToList();
        var descriptions = achievements.Select(a => Norm(a.Description)).ToList();
        // Names long enough not to be common words, to notice another achievement mentioned in a walkthrough's step.
        var mentions = achievements.Select(a => NamesOf(a).Select(Norm).Where(n => n.Length >= 6).ToList()).ToList();

        var heads = new List<(int Section, int Line, string Text)>();
        for (int s = 0; s < sections.Count; s++)
        {
            heads.Add((s, -1, sections[s].Title));
            for (int l = 0; l < sections[s].Lines.Count; l++)
                if (IsHeading(sections[s].Lines[l])) heads.Add((s, l, sections[s].Lines[l].Text));
        }
        var folded = sections.Select(s => s.Lines.Select(l => Fold(l.Text)).ToList()).ToList();

        var tips = new Dictionary<string, GuideTip>(StringComparer.Ordinal);
        for (int k = 0; k < achievements.Count; k++)
        {
            var a = achievements[k];
            if (names[k].Length == 0) continue;
            var places = new List<(int Rank, int Section, int Line)>();
            foreach (var (s, l, text) in heads)
                if (Tier(text, a.Name, names[k]) is { } tier) places.Add((tier * 2 + (l >= 0 ? 0 : 1), s, l));
            if (starts[k] is { } start)
                for (int s = 0; s < sections.Count; s++)
                    for (int l = 0; l < sections[s].Lines.Count; l++)
                        if (!places.Any(p => p.Section == s && p.Line == l) && start.IsMatch(folded[s][l])) places.Add((20, s, l));
            if (places.Count == 0 && ByDescription(sections, a) is { } d) places.Add(d);
            if (places.Count == 0) continue;

            // The surest kind of place, and of those the one with the most words: a contents list names it too.
            int kind = places.Min(p => Kind(p.Rank));
            var best = places
                .Where(p => Kind(p.Rank) == kind)
                .Select(p => (p.Rank, p.Section, Lines: Quote(sections[p.Section], folded[p.Section], p.Line, a, k, stops, descriptions, mentions)))
                .Select(x => (x.Rank, x.Section, x.Lines, Text: Words(x.Lines, a)))
                .OrderByDescending(x => x.Text.Length).ThenBy(x => x.Rank)
                .First();
            tips[a.ApiName] = new GuideTip(page.Id, page.Title, page.Author, language, sections[best.Section].Id, best.Rank, best.Text)
                { Pictures = PicturesOf(best.Lines) };
        }

        foreach (var subject in TitleSubjects(page.Title, names, Norm(game)))
        {
            var a = achievements[subject];
            if (tips.TryGetValue(a.ApiName, out var known) && known.Text.Length > 0) continue;
            // From where the guide first names it: before that come the foreword and other achievements.
            var all = sections.SelectMany(s => s.Lines).ToList();
            int from = all.FindIndex(l => (" " + Norm(l.Text) + " ").Contains(" " + names[subject] + " ", StringComparison.Ordinal));
            var rest = all.Skip(Math.Max(0, from)).ToList();
            var whole = Words(rest, a);
            // A guide of only pictures is still the place to look.
            if (whole.Length > 0 || known is null)
                tips[a.ApiName] = new GuideTip(page.Id, page.Title, page.Author, language, "", TitleRank, whole) { Pictures = PicturesOf(rest) };
        }
        return tips;
    }

    /// <summary>Between a heading and a line that only starts with the name.</summary>
    private const int TitleRank = 10;

    /// <summary>Kinds of places by rank: a heading naming it, a heading nearly naming it, a line starting with the name, its description.</summary>
    private static int Kind(int rank) => rank < 6 ? 0 : rank < 20 ? 1 : rank < 30 ? 2 : 3;

    /// <summary>The few achievements a title names, but not by the game's own name; a title naming many is a general guide.</summary>
    private static List<int> TitleSubjects(string title, IReadOnlyList<string> names, string game)
    {
        var t = Norm(title);
        var found = Enumerable.Range(0, names.Count).Where(k => names[k] != game && Names(t, names[k])).ToList();
        return found.Count <= 3 ? found : [];
    }

    /// <summary>The title is the name, has it as whole words, or a long name nearly (titles misspell numbers).</summary>
    private static bool Names(string title, string name)
    {
        if (name == title) return true;
        if (name.Length < 4) return false;
        if ((" " + title + " ").Contains(" " + name + " ", StringComparison.Ordinal)) return true;
        if (name.Length < 10) return false;
        var words = title.Split(' ');
        int count = name.Split(' ').Length;
        for (int len = Math.Max(1, count - 1); len <= count + 1; len++)
            for (int i = 0; i + len <= words.Length; i++)
                if (Near(string.Join(' ', words, i, len), name, NearMiss)) return true;
        return false;
    }

    /// <summary>A line that looks like a heading: a heading block, mostly bold or underlined, ending with a colon, or in capitals.</summary>
    public static bool IsHeading(GuideLine line)
    {
        var t = line.Text;
        if (t.Length == 0 || t.Length > 90) return false;
        if (line.Heading > 0 || line.Bold >= 0.8 || line.Underlined >= 0.8) return true;
        if (t.EndsWith(':') && t.Length <= 60) return true;
        var letters = t.Where(char.IsLetter).ToList();
        return letters.Count >= 4 && letters.All(char.IsUpper) && t.Length <= 60;
    }

    /// <summary>How well a heading names the achievement: 0 - exactly, 1 - normalized, 2 - as a part, 3 - nearly; null - not it.</summary>
    private static int? Tier(string raw, string name, string norm)
    {
        raw = raw.Trim();
        if (raw == name) return 0;
        var stripped = NumberPrefix().Replace(raw, "");
        var whole = Norm(stripped);
        if (whole == norm) return 1;
        // "Name - description", "Chapter I: Name", "Name (DLC)".
        var parts = Separator().Split(stripped).Select(Norm).ToList();
        if (parts.Contains(norm)) return 2;
        // "World 5" is not a misspelled "World 6".
        if (norm.Length >= 6 && parts.Prepend(whole).Any(part => Near(part, norm, NearMiss) && Numbers(part).SequenceEqual(Numbers(norm)))) return 3;
        return null;
    }

    private static IEnumerable<string> Numbers(string text) => Digits().Matches(text).Select(m => m.Value);

    /// <summary>A line repeating the description, with the same numbers in it: the heading above it, or the line itself.</summary>
    private static (int Rank, int Section, int Line)? ByDescription(IReadOnlyList<GuideSection> sections, GuideAchievement a)
    {
        var desc = Norm(a.Description);
        if (desc.Length < 15) return null;
        var digits = Numbers(desc).ToList();
        for (int s = 0; s < sections.Count; s++)
        {
            var lines = sections[s].Lines;
            for (int l = 0; l < lines.Count; l++)
            {
                var n = Norm(lines[l].Text);
                if (n.Length < 10 || !Near(n, desc, 0.88) || !Numbers(n).SequenceEqual(digits)) continue;
                return l > 0 && IsHeading(lines[l - 1]) ? (30, s, l - 1) : (31, s, l);
            }
        }
        return null;
    }

    /// <summary>The first pictures of the quoted lines: a walkthrough of pictures shows only its start.</summary>
    private static List<string> PicturesOf(IEnumerable<GuideLine> lines) => lines.SelectMany(l => l.Pictures).Distinct().Take(MaxPictures).ToList();

    private const int MaxPictures = 8;

    /// <summary>
    /// The lines from the place on, or in a walkthrough the steps leading up to it: the quote is their words without
    /// lines that only repeat the name or the description (<see cref="Words"/>).
    /// </summary>
    private static List<GuideLine> Quote(GuideSection section, List<string> folded, int line, GuideAchievement a, int self,
        List<List<Regex>> stops, List<string> descriptions, List<List<string>> mentions)
    {
        var lines = section.Lines;
        var block = new List<GuideLine>();
        if (line < 0)
        {
            block.AddRange(lines.Take(MaxLines));
        }
        else if (LeadIn(lines, line) is { } lead)
        {
            // A walkthrough leads up to the achievement ("...and you get the achievement:") and goes on after its name:
            // the words are the steps before it, back to a heading or a line about another achievement.
            int chars = 0;
            for (int i = lead; i >= 0 && block.Count < MaxLines; i--)
            {
                var n = " " + Norm(lines[i].Text) + " ";
                // Cut from the far end: the steps nearest the achievement matter most.
                if (i < lead && (IsHeading(lines[i]) || chars + lines[i].Text.Length > MaxChars
                    || stops.Where((_, k) => k != self).Any(r => r.Any(p => p.IsMatch(folded[i])))
                    || descriptions.Where((_, k) => k != self).Any(d => d.Length >= 10 && n == " " + d + " ")
                    || mentions.Where((_, k) => k != self).Any(m => m.Any(name => n.Contains(" " + name + " ", StringComparison.Ordinal))))) break;
                // Ending the quote, the lead-in is a sentence, not a label of what follows.
                block.Insert(0, i == lead ? lines[i] with { Text = lines[i].Text.TrimEnd()[..^1].TrimEnd() + "." } : lines[i]);
                chars += lines[i].Text.Length;
            }
        }
        else
        {
            // Of the entry's own line only what follows the name: "Name - how to get it".
            var text = lines[line].Text;
            block.Add(lines[line] with { Text = AfterName(text, a.Name) ?? AfterNearName(text, a.Name) ?? text });
            // An entry that is a list item ends with it.
            int item = lines[line].Item;
            for (int i = line + 1; i < lines.Count && block.Count < MaxLines; i++)
            {
                if (item > 0 && lines[i].Item != item || IsHeading(lines[i])) break;
                if (stops.Where((_, k) => k != self).Any(r => r.Any(p => p.IsMatch(folded[i])))) break;
                block.Add(lines[i]);
            }
        }

        return block;
    }

    /// <summary>The line before the place, past pictures, when it leads up to the achievement; null - it does not.</summary>
    private static int? LeadIn(IReadOnlyList<GuideLine> lines, int line)
    {
        int i = line - 1;
        while (i >= 0 && lines[i].Text.Length == 0) i--;
        return i >= 0 && LeadsUp().IsMatch(lines[i].Text) ? i : null;
    }

    private static IEnumerable<string> NamesOf(GuideAchievement a) =>
        a.OtherName.Length > 0 && a.OtherName != a.Name ? [a.Name, a.OtherName] : [a.Name];

    /// <summary>The text after the achievement's name, without separators; null - the name is not in it.</summary>
    private static string? AfterName(string text, string name)
    {
        var words = Words().Matches(name).Select(m => Regex.Escape(m.Value)).ToList();
        if (words.Count == 0) return null;
        var m = Regex.Match(text, string.Join(@"[\W_]+", words), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? Rest(text[(m.Index + m.Length)..]) : null;
    }

    /// <summary>The text after a part that nearly is the name (a misspelled heading); null - no part is.</summary>
    private static string? AfterNearName(string text, string name)
    {
        var norm = Norm(name);
        var separators = Separator().Matches(text);
        int start = 0;
        for (int i = 0; i <= separators.Count; i++)
        {
            int end = i < separators.Count ? separators[i].Index : text.Length;
            if (Near(Norm(NumberPrefix().Replace(text[start..end], "")), norm, NearMiss)) return Rest(text[end..]);
            if (i < separators.Count) start = separators[i].Index + separators[i].Length;
        }
        return null;
    }

    private static string Rest(string after)
    {
        // Bilingual guides follow the name with a slash and the name in their other language.
        if (after.TrimStart().StartsWith('/')) return "";
        var rest = Leading().Replace(after, "");
        return rest.EndsWith(')') && !rest.Contains('(') ? rest[..^1] : rest;
    }

    /// <summary>
    /// The text of the lines without those that add nothing: the name, the description, links, lines without
    /// words ("1:31"), a video's timestamps, and labels ("Description:") of such lines. Cut to size.
    /// </summary>
    private static string Words(IEnumerable<GuideLine> lines, GuideAchievement a)
    {
        var names = NamesOf(a).Select(Norm).ToList();
        var desc = Norm(a.Description);
        var list = lines.ToList();
        bool videoIndex = list.Count(l => Timestamp().IsMatch(l.Text)) >= 2;
        var sb = new StringBuilder();
        void Add(string text)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        string? label = null;
        foreach (var l in list)
        {
            var n = Norm(NumberPrefix().Replace(l.Text, ""));
            bool nothing = !n.Any(char.IsLetter) || names.Contains(n) || n == desc || LinkOnly(l.Text) || videoIndex && Timestamp().IsMatch(l.Text)
                || desc.Length > 0 && (Near(n, desc, NearMiss) || n.Contains(desc, StringComparison.Ordinal) && n.Length - desc.Length < 8 || Retells(n, desc))
                || names.Any(name => n.StartsWith(name + " ", StringComparison.Ordinal) && Near(n[(name.Length + 1)..], desc, NearMiss));
            if (nothing)
            {
                label = null;
                continue;
            }
            if (l.Text.EndsWith(':') && l.Text.Length <= 60)
            {
                label = l.Text;
                continue;
            }
            if (label is not null) Add(label);
            Add(l.Text);
            label = null;
            if (sb.Length >= MaxChars) break;
        }
        return Cut(sb.ToString());
    }

    /// <summary>A line that is only a link, maybe after a short label ("Video:", "Cassettes -"): a video or a picture, not words to quote.</summary>
    private static bool LinkOnly(string text)
    {
        var rest = Link().Replace(text, "").Trim();
        if (rest.Length == text.Trim().Length) return false;
        return rest.Length == 0 || LabelEnd().IsMatch(rest) && Words().Matches(rest).Count <= 4;
    }

    /// <summary>A short line of the description's words, all but a quarter of them: the description retold.</summary>
    private static bool Retells(string line, string desc)
    {
        var words = line.Split(' ');
        var known = desc.Split(' ');
        if (words.Length > known.Length + 2) return false;
        var stems = known.Select(Stem).ToHashSet(StringComparer.Ordinal);
        return words.Count(w => !stems.Contains(Stem(w))) <= words.Length / 4;
    }

    /// <summary>Enough of a word to match its other forms: "confirm" and "confirmed", "подтвердить" and "подтвердите".</summary>
    private static string Stem(string word) => word.Length > 4 ? word[..4] : word;

    /// <summary>At most <see cref="MaxChars"/> characters, cut at a word.</summary>
    private static string Cut(string text)
    {
        if (text.Length <= MaxChars) return text;
        int at = text.LastIndexOf(' ', MaxChars);
        return text[..(at > MaxChars / 2 ? at : MaxChars)].TrimEnd(' ', ',', ';', ':', '-') + "...";
    }

    /// <summary>
    /// A line starting with the name: maybe a number before it, maybe the name in another language and a slash
    /// (bilingual guides), then a separator or the end.
    /// </summary>
    private static Regex? StartPattern(string name)
    {
        var tokens = Words().Matches(name).Select(m => Norm(m.Value)).Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0) return null;
        var body = string.Join(@"[\W_]+", tokens.Select(Regex.Escape));
        return new Regex(@"^[\W_]*(?:\d+[\W_]+)?(?:[^/]{0,60}/\s*)?" + body + @"[!?.]*\s*(?:[:\p{Pd}|(\[/.,]|$)");
    }

    /// <summary>Lowercase without accents.</summary>
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormKD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// Folded, apostrophes dropped, punctuation turned into single spaces. In Cyrillic words Latin look-alikes
    /// become Cyrillic: guides mix them up.
    /// </summary>
    public static string Norm(string s)
    {
        s = Apostrophes().Replace(Fold(s).Replace("&", " and "), "");
        s = NonWord().Replace(s, " ").Trim();
        if (s.Length == 0) return s;
        var words = s.Split(' ');
        for (int i = 0; i < words.Length; i++)
            if (words[i].Any(c => c is >= (char)0x400 and <= (char)0x4FF))
                words[i] = string.Concat(words[i].Select(c => LatinToCyrillic.TryGetValue(c, out var r) ? r : c));
        return string.Join(' ', words);
    }

    /// <summary>Latin letters that look like Cyrillic ones, to those Cyrillic letters (by code: they look the same).</summary>
    private static readonly Dictionary<char, char> LatinToCyrillic = "abcehkmoptxy"
        .Zip([0x430, 0x432, 0x441, 0x435, 0x43D, 0x43A, 0x43C, 0x43E, 0x440, 0x442, 0x445, 0x443], (latin, code) => (latin, (char)code))
        .ToDictionary(p => p.latin, p => p.Item2);

    /// <summary>Similar at least by the cut; lengths too far apart are not compared at all.</summary>
    private static bool Near(string a, string b, double cut) =>
        a.Length > 0 && b.Length > 0 && Math.Abs(a.Length - b.Length) <= (1 - cut) * Math.Max(a.Length, b.Length) && Similarity(a, b) >= cut;

    /// <summary>1 minus the edit distance over the longer length.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return a.Length == b.Length ? 1 : 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }

    [GeneratedRegex(@"^\W*(?:\d+\s*[\).:\-]\s*)")]
    private static partial Regex NumberPrefix();

    [GeneratedRegex(@"\s+[\p{Pd}|]\s+|\s*[:|]\s+|\s*[(\[]")]
    private static partial Regex Separator();

    [GeneratedRegex(@"[\p{Pi}\p{Pf}\p{Sk}']")]
    private static partial Regex Apostrophes();

    [GeneratedRegex(@"[\W_]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"^[\s\p{Pd}:|.,;!?/(\[]+")]
    private static partial Regex Leading();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Link();

    [GeneratedRegex(@"[:\p{Pd}]$")]
    private static partial Regex LabelEnd();

    /// <summary>
    /// "...and you get the achievement:", "...то получите достижение:": getting one achievement, not a list
    /// ("Here are the achievements:") or a turn to the next entry ("Рассмотрим следующее достижение:").
    /// </summary>
    [GeneratedRegex(@"\b(?:get|got|unlock|earn|receiv|obtain|award|pop|получ|откро|выда|дадут|заработа)\w*[^.!?]*\b(?:achievement|trophy|достижение|ачивку|ачивка)\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LeadsUp();

    /// <summary>A line starting with a time in a video: "08:46 - Yoyo".</summary>
    [GeneratedRegex(@"^\W*(?:\d{1,2}:)?\d{1,2}:\d{2}\b")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"\w+")]
    private static partial Regex Words();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
