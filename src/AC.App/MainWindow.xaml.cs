using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private HotkeyManager? _hotkeys;
    private OverlayWindow? _overlay;
    /// <summary>The hotkeys that could not be registered: taken by another program, or not a valid combination.</summary>
    private string? _hotkeyProblem;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(GameService.CreateDefault());
        DataContext = _vm;
        _vm.Toast += text => _overlay?.ShowToast(text);
        Loaded += async (_, _) =>
        {
            await _vm.StartAsync();
            // The statuses of the start would hide it.
            if (_hotkeyProblem is { } problem) _vm.StatusMessage = problem;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeys = new HotkeyManager(new WindowInteropHelper(this).Handle);
        RegisterHotkeys();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterAll();
        var s = _vm.Service.Settings;
        var problems = new List<string>();
        if (!_hotkeys.Register(s.OverlayToggleHotkey, ToggleOverlay)) problems.Add(s.OverlayToggleHotkey);
        if (!_hotkeys.Register(s.OverlayInteractHotkey, () => EnsureOverlay().ToggleInteractive())) problems.Add(s.OverlayInteractHotkey);
        _hotkeyProblem = problems.Count > 0 ? Loc.T("status.hotkeysFailed", string.Join(", ", problems)) : null;
        if (_hotkeyProblem is not null) _vm.StatusMessage = _hotkeyProblem;
    }

    private OverlayWindow EnsureOverlay()
    {
        if (_overlay is null)
        {
            _overlay = new OverlayWindow(_vm);
            _overlay.Closed += (_, _) => _overlay = null;
        }
        return _overlay;
    }

    private void ToggleOverlay()
    {
        var o = EnsureOverlay();
        if (o.IsVisible) o.Hide();
        else o.Show();
    }

    private void OnToggleOverlay(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_vm.Service) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Loc.SetLanguage(_vm.Service.Settings.UiLanguage);
        ThemeManager.Apply(_vm.Service.Settings.Theme);
        _vm.SettingsChanged();
        RegisterHotkeys();
        _overlay?.ApplySettings();
        _vm.StatusMessage = _hotkeyProblem ?? Loc.T("status.settingsSaved");
    }

    /// <summary>A guide picture opens larger, with the tip's other pictures to page through.</summary>
    private void OnOpenPicture(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GuidePictureViewModel picture } && _vm.SelectedRow is { } row)
            new PictureWindow(row.GuidePictures, picture, row.Name, this).ShowDialog();
    }

    /// <summary>The list context menu only holds Steam writes; no writes, no menu.</summary>
    private void OnAchievementMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!_vm.ManualUnlocks) e.Handled = true;
    }

    /// <summary>A drop-down button: the menu is its ContextMenu, opened on a normal click.</summary>
    private void OnDropDown(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _overlay?.SavePosition();
        // Unregister the hotkeys while the window is alive (UnregisterHotKey fails once it is destroyed).
        _hotkeys?.Dispose();
        _hotkeys = null;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _overlay?.Close();
        _vm.Dispose();
        base.OnClosed(e);
    }
}
