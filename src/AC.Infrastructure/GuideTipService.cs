using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

/// <summary>Tips for achievements quoted from Steam Community guides, kept per game, and their translations.</summary>
[SupportedOSPlatform("windows")]
public sealed class GuideTipService(SteamWebApi api, JsonStore store, AppSettings settings)
{
    private static readonly TimeSpan GuidesTtl = TimeSpan.FromDays(30);

    private readonly SteamGuides _guides = new(api);
    private readonly Translator _translator = new(new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
    /// <summary>When reading a game's guides last failed: not tried again for a day.</summary>
    private readonly Dictionary<uint, DateTimeOffset> _failed = new();

    /// <summary>
    /// The tips in the content language, English ones where those have no words of their own. Kept for a month and
    /// read again when the content language changes. Null - no key and nothing kept.
    /// </summary>
    public async Task<GuideTips?> GetAsync(GameData game, CancellationToken ct = default)
    {
        var kept = store.LoadGuides(game.AppId);
        var language = settings.ContentLanguage;
        bool fresh = kept is not null && kept.Format == GuideTips.CurrentFormat && kept.Language == language &&
                     DateTimeOffset.Now - kept.FetchedAt < GuidesTtl;
        bool failed = _failed.TryGetValue(game.AppId, out var at) && DateTimeOffset.Now - at < TimeSpan.FromDays(1);
        if (fresh || failed || !settings.HasApiKey || game.Achievements.Count == 0) return kept;
        try
        {
            var key = settings.ApiKey;
            bool english = string.Equals(language, "english", StringComparison.OrdinalIgnoreCase);
            var names = english ? null : await EnglishNamesAsync(game, key, ct);
            var englishName = (names ?? []).DistinctBy(n => n.ApiName).ToDictionary(n => n.ApiName, n => n.Name, StringComparer.Ordinal);
            var own = game.Achievements
                .Select(a => new GuideAchievement(a.ApiName, a.Name, a.Description, englishName.GetValueOrDefault(a.ApiName, "")))
                .ToList();
            var mine = english ? new Dictionary<string, GuideTip>() : await _guides.TipsAsync(key, game.AppId, game.Name, language, own, ct);
            var theirs = (english ? own : names) is { } list
                ? await _guides.TipsAsync(key, game.AppId, game.Name, "english", list, ct)
                : new Dictionary<string, GuideTip>();
            var tips = new GuideTips
            {
                Format = GuideTips.CurrentFormat, Language = language, FetchedAt = DateTimeOffset.Now, Tips = GuideTips.Combine(mine, theirs),
            };
            store.SaveGuides(game.AppId, tips);
            return tips;
        }
        catch (Exception e) when (e is SteamApiException or HttpRequestException or TaskCanceledException)
        {
            _failed[game.AppId] = DateTimeOffset.Now;
            AppLog.Info($"{game.AppId}: guides: {e.Message}");
            return kept;
        }
    }

    /// <summary>
    /// A tip in another language (ISO code), translated once and kept with the tips.
    /// Null - no words to translate, or they already are in that language.
    /// </summary>
    public async Task<string?> TranslateAsync(uint appId, GuideTips tips, string apiName, string language, CancellationToken ct = default)
    {
        var key = language + ":" + apiName;
        if (tips.Translations.TryGetValue(key, out var done)) return done;
        if (!tips.Tips.TryGetValue(apiName, out var tip) || tip.Text.Length == 0) return null;
        var text = await _translator.TranslateAsync(tip.Text, language, ct);
        if (text is null) return null;
        tips.Translations[key] = text;
        store.SaveGuides(appId, tips);
        return text;
    }

    /// <summary>English names to find achievements in English guides: from the local schema, else from the Web API.</summary>
    private async Task<List<GuideAchievement>?> EnglishNamesAsync(GameData game, string key, CancellationToken ct)
    {
        if (SteamLocal.ReadSchema(game.AppId, "english") is { Count: > 0 } local)
            return local.Select(a => new GuideAchievement(a.ApiName, a.Name, a.Description)).ToList();
        var (_, entries) = await api.GetSchemaAsync(key, game.AppId, "english", ct);
        return entries.Count == 0 ? null : entries.Select(e => new GuideAchievement(e.ApiName, e.Name, e.Description)).ToList();
    }
}
