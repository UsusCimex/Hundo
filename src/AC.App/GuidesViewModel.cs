using System.Globalization;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>
/// Tips for the open game's achievements quoted from Steam guides and their translations, and searches for how to get
/// an achievement.
/// </summary>
public sealed class GuidesViewModel
{
    private readonly MainViewModel _main;
    private readonly AppServices _svc;
    private GuideTips? _tips;

    public GuidesViewModel(MainViewModel main, AppServices svc)
    {
        _main = main;
        _svc = svc;
        SearchCommand = new RelayCommand(p => Search(p as string), _ => main.Game != null && main.SelectedRow != null);
        OpenTipCommand = new RelayCommand(p => main.OpenUrl(p is AchievementRowViewModel r ? TipFor(r.ApiName)?.Url : null));
        TranslateCommand = new AsyncCommand(p => TranslateAsync(p as AchievementRowViewModel), main.OnError);
    }

    /// <summary>Look up how to get the selected achievement online: steam, discussions, youtube, google.</summary>
    public RelayCommand SearchCommand { get; }
    /// <summary>Opens the guide section a tip comes from. Parameter: the row.</summary>
    public RelayCommand OpenTipCommand { get; }
    /// <summary>Shows a tip in the interface language, or the original again. Parameter: the row.</summary>
    public AsyncCommand TranslateCommand { get; }

    public GuideTip? TipFor(string apiName) => _tips?.Tips.GetValueOrDefault(apiName);

    public string? TranslationOf(string apiName) => _tips?.Translations.GetValueOrDefault(Loc.Language + ":" + apiName);

    /// <summary>Another game is open: its tips are not read yet.</summary>
    public void Clear() => _tips = null;

    /// <summary>Tips from Steam guides, read in the background: the rows show them when they arrive.</summary>
    public async Task LoadAsync(GameData game)
    {
        var tips = await _svc.Guides.GetAsync(game);
        if (!ReferenceEquals(game, _main.Game)) return;
        _tips = tips;
        foreach (var row in _main.Rows.Concat(_main.Overlay.Nearest)) row.RefreshGuide();
    }

    /// <summary>Translates on demand; when the translator does not answer, the browser translates instead.</summary>
    private async Task TranslateAsync(AchievementRowViewModel? row)
    {
        if (row is null || _main.Game is not { } game || _tips is not { } tips) return;
        if (row.ShowsTranslation)
        {
            row.ShowTranslation(false);
            return;
        }
        try
        {
            if (await _svc.Guides.TranslateAsync(game.AppId, tips, row.ApiName, Loc.Language) is not null)
            {
                row.ShowTranslation(true);
                foreach (var shown in _main.Overlay.Nearest.Where(r => r.ApiName == row.ApiName)) shown.RefreshGuide();
            }
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppLog.Info($"Translation: {e.Message}");
            _main.StatusMessage = Loc.T("status.translateInBrowser");
            _main.OpenUrl(Translator.WebUrl(TipFor(row.ApiName)?.Text ?? "", Loc.Language));
        }
    }

    private void Search(string? kind)
    {
        if (_main.Game is not { } open || _main.SelectedRow is not { } row) return;
        var game = open.Name.Length > 0 ? open.Name : open.AppId.ToString(CultureInfo.InvariantCulture);
        var ach = row.Name;
        string q(string s) => Uri.EscapeDataString(s);
        var url = kind switch
        {
            "steam" => $"https://steamcommunity.com/app/{open.AppId}/guides/?searchText={q(ach)}&browsefilter=trend&requiredtags%5B%5D=-1",
            "discussions" => $"https://steamcommunity.com/app/{open.AppId}/discussions/search/?q={q(ach)}",
            "youtube" => $"https://www.youtube.com/results?search_query={q(game + " " + ach + " achievement")}",
            _ => $"https://www.google.com/search?q={q("\"" + game + "\" \"" + ach + "\" achievement guide")}",
        };
        _main.OpenUrl(url);
    }
}
