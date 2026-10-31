using System.Diagnostics;
using System.Globalization;
using System.Windows;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

public partial class SettingsWindow : Window
{
    private readonly GameService _svc;

    public SettingsWindow(GameService svc)
    {
        InitializeComponent();
        WindowFit.ToWorkArea(this);
        _svc = svc;
        var s = svc.Settings;
        ApiKeyBox.Password = svc.ApiKey;
        SteamIdBox.Text = s.SteamId64 == 0 ? "" : s.SteamId64.ToString(CultureInfo.InvariantCulture);
        LanguageBox.SelectedItem = Options.ContentLanguages.FirstOrDefault(l => l.Value == s.Language) ?? Options.ContentLanguages[0];
        UiLanguageBox.SelectedItem = Options.UiLanguages.FirstOrDefault(l => l.Value == s.UiLanguage) ?? Options.UiLanguages[0];
        ManualUnlocksBox.IsChecked = s.ManualUnlocks;
        WriteModeBox.SelectedItem = Options.WriteModes.FirstOrDefault(m => m.Value == s.WriteMode) ?? Options.WriteModes[0];
        AutoMarkBox.IsChecked = s.AutoMark;
        StatusSourceBox.SelectedItem = Options.StatusSources.FirstOrDefault(m => m.Value == s.StatusSource) ?? Options.StatusSources[0];
        ThemeBox.SelectedItem = Options.Themes.FirstOrDefault(t => t.Value == s.Theme) ?? Options.Themes[0];
        HideSpoilersBox.IsChecked = s.HideSpoilers;
        CheckUpdatesBox.IsChecked = s.CheckUpdates;
        ToggleHotkeyBox.Text = s.OverlayToggleHotkey;
        InteractHotkeyBox.Text = s.OverlayInteractHotkey;
        OpacitySlider.Value = s.OverlayOpacity;
        NearestBox.Text = s.OverlayNearestCount.ToString(CultureInfo.InvariantCulture);
        PollBox.Text = s.PollSeconds.ToString(CultureInfo.InvariantCulture);
        DataDirBox.Text = AppPaths.DataRoot;
        DataDirHint.Text = AppPaths.IsPortable ? Loc.T("settings.dataPortable") : Loc.T("settings.dataHint", AppPaths.DataDirVariable);
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppPaths.DataRoot) { UseShellExecute = true });

    private void OnGetKey(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://steamcommunity.com/dev/apikey") { UseShellExecute = true });

    private void OnManualUnlocksClick(object sender, RoutedEventArgs e)
    {
        if (ManualUnlocksBox.IsChecked != true) return;
        var answer = MessageBox.Show(this, Loc.T("dialog.manualUnlocks"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) ManualUnlocksBox.IsChecked = false;
    }

    private void OnAutoMarkClick(object sender, RoutedEventArgs e)
    {
        if (AutoMarkBox.IsChecked != true) return;
        var answer = MessageBox.Show(this, Loc.T("dialog.autoMark"), Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) AutoMarkBox.IsChecked = false;
    }

    private void OnDetectSteamId(object sender, RoutedEventArgs e)
    {
        var u = SteamLocal.MostRecentUser();
        if (u is null)
        {
            MessageBox.Show(this, Loc.T("dialog.noLoginUsers"), Title);
            return;
        }
        SteamIdBox.Text = u.Value.SteamId.ToString(CultureInfo.InvariantCulture);
        MessageBox.Show(this, Loc.T("dialog.accountFound", u.Value.PersonaName), Title);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = _svc.Settings;

        var idText = SteamIdBox.Text.Trim();
        ulong steamId = 0;
        if (idText.Length > 0 && (!ulong.TryParse(idText, out steamId) || steamId < SteamLocal.SteamId64Base))
        {
            MessageBox.Show(this, Loc.T("dialog.badSteamId"), Title);
            return;
        }

        foreach (var hk in new[] { ToggleHotkeyBox.Text, InteractHotkeyBox.Text })
        {
            if (!HotkeyManager.TryParse(hk, out _, out _))
            {
                MessageBox.Show(this, Loc.T("dialog.badHotkey", hk), Title);
                return;
            }
        }

        _svc.ApiKey = ApiKeyBox.Password;
        s.SteamId64 = steamId;
        s.Language = (LanguageBox.SelectedItem as Option<string>)?.Value ?? "";
        s.UiLanguage = (UiLanguageBox.SelectedItem as Option<string>)?.Value ?? Loc.Fallback;
        s.ManualUnlocks = ManualUnlocksBox.IsChecked == true;
        s.WriteMode = (WriteModeBox.SelectedItem as Option<WriteMode>)?.Value ?? WriteMode.MarkOnly;
        s.AutoMark = AutoMarkBox.IsChecked == true;
        s.StatusSource = (StatusSourceBox.SelectedItem as Option<StatusSource>)?.Value ?? StatusSource.WebApi;
        s.Theme = (ThemeBox.SelectedItem as Option<AppTheme>)?.Value ?? AppTheme.System;
        s.HideSpoilers = HideSpoilersBox.IsChecked == true;
        s.CheckUpdates = CheckUpdatesBox.IsChecked == true;
        s.OverlayToggleHotkey = ToggleHotkeyBox.Text.Trim();
        s.OverlayInteractHotkey = InteractHotkeyBox.Text.Trim();
        s.OverlayOpacity = OpacitySlider.Value;
        if (int.TryParse(NearestBox.Text.Trim(), out var n) && n is > 0 and <= 20) s.OverlayNearestCount = n;
        if (int.TryParse(PollBox.Text.Trim(), out var poll) && poll >= 15) s.PollSeconds = poll;
        _svc.SaveSettings();
        DialogResult = true;
    }
}
