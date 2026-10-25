using AC.Core;

namespace AC.Infrastructure;

/// <summary>
/// Steam Community guides through the Web API: a game's guides with the Achievements tag in a language,
/// those whose titles promise achievements, and their sections, until every achievement has words. Needs the key.
/// </summary>
public sealed class SteamGuides(SteamWebApi api)
{
    private const int ListSize = 50;
    private const int MaxGuides = 5;

    /// <summary>The best tip per API name from the guides in one Steam language; empty when there are none.</summary>
    public async Task<Dictionary<string, GuideTip>> TipsAsync(
        string key, uint appId, string gameName, string language, IReadOnlyList<GuideAchievement> achievements, CancellationToken ct = default)
    {
        var tips = new Dictionary<string, GuideTip>(StringComparer.Ordinal);
        // Guides carry their language as a tag named like Steam's language.
        var picked = GuideParser.Pick(await api.QueryGuidesAsync(key, appId, ["Achievements", language], ListSize, ct), MaxGuides);
        if (picked.Count == 0) return tips;
        var authors = await api.GetPlayerNamesAsync(key, picked.Select(g => g.Creator).Where(c => c != 0).Distinct().ToList(), ct);
        foreach (var card in picked)
        {
            if (achievements.All(a => tips.TryGetValue(a.ApiName, out var t) && t.Text.Length > 0)) break;
            var sections = (await api.GetGuideSectionsAsync(key, card.Id, ct))
                .Select(s => new GuideSection(s.Id, s.Title, GuideParser.Lines(s.Text, card.Pictures)))
                .ToList();
            var page = new GuidePage(card.Id, card.Title, authors.GetValueOrDefault(card.Creator, ""), sections);
            foreach (var (apiName, tip) in GuideMatcher.Match(page, language, achievements, gameName))
                if (!tips.TryGetValue(apiName, out var known) || tip.IsBetterThan(known)) tips[apiName] = tip;
        }
        return tips;
    }
}
