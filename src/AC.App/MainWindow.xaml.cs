using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private TrayIcon? _tray;
    private readonly bool _startedInTray;
    private bool _started;
    private bool _quitting;

    /// <param name="inTray">Started with Windows: no window, the app watches for games from the notification area.</param>
    public MainWindow(bool inTray = false)
    {
        InitializeComponent();
        WindowFit.ToWorkArea(this);
        _vm = new MainViewModel(GameService.CreateDefault());
        DataContext = _vm;
        _vm.Toast += OnToast;
        _vm.PlayingChanged += OnPlayingChanged;
        _startedInTray = inTray;
        Loaded += async (_, _) => await StartAsync();
        UpdateTray();
        if (inTray)
        {
            new WindowInteropHelper(this).EnsureHandle();
            _ = StartAsync();
        }
    }

    private async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        await _vm.StartAsync();
        // The statuses of the start would hide it.
        if (_hotkeyProblem is { } problem) _vm.StatusMessage = problem;
    }

    /// <summary>The icon is there while closing the window keeps the app running, and after a start with Windows.</summary>
    private void UpdateTray()
    {
        _tray?.Dispose();
        _tray = null;
        if (!_vm.Service.Settings.CloseToTray && !_startedInTray) return;
        var open = new MenuItem { Header = Loc.T("tray.open"), FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowFromTray();
        var quit = new MenuItem { Header = Loc.T("tray.quit") };
        quit.Click += (_, _) => Quit();
        _tray = new TrayIcon("Hundo", new ContextMenu { Items = { open, quit } });
        _tray.Click += ShowFromTray;
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Closing from now on quits: the tray's Quit, or Windows ending the session.</summary>
    public void AllowQuit() => _quitting = true;

    private void Quit()
    {
        _quitting = true;
        Close();
    }

    private void HintTrayOnce()
    {
        var s = _vm.Service.Settings;
        if (s.TrayHintShown || _tray is null) return;
        s.TrayHintShown = true;
        _vm.Service.SaveSettings();
        _tray.ShowInfo(Loc.T("tray.hintTitle"), Loc.T("tray.hint"));
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
            _overlay.ToastEnded += OnToastEnded;
        }
        return _overlay;
    }

    private void ToggleOverlay()
    {
        _overlayForGame = _overlayForToast = false;
        var o = EnsureOverlay();
        if (o.IsVisible) o.HideToGame();
        else o.Show();
    }

    // Shown on its own for the game or for a message: hidden again on its own, unless the user toggled it since.
    private bool _overlayForGame;
    private bool _overlayForToast;

    private void OnPlayingChanged(bool playing)
    {
        if (!_vm.Service.Settings.OverlayAuto) return;
        var o = EnsureOverlay();
        if (playing && !o.IsVisible)
        {
            o.Show();
            _overlayForGame = true;
        }
        else if (!playing && (_overlayForGame || _overlayForToast))
        {
            o.HideToGame();
            _overlayForGame = _overlayForToast = false;
        }
    }

    /// <summary>A message mid-game shows the hidden overlay until the message goes.</summary>
    private void OnToast(string text)
    {
        if (_vm.Service.Settings.OverlayAuto && _vm.Playing && EnsureOverlay() is { IsVisible: false } hidden)
        {
            hidden.Show();
            _overlayForToast = true;
        }
        _overlay?.ShowToast(text);
    }

    private void OnToastEnded()
    {
        if (!_overlayForToast) return;
        _overlayForToast = false;
        if (!_overlayForGame) _overlay?.HideToGame();
    }

    private void OnToggleOverlay(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_vm.Service) { Owner = this };
        // Off while it is open: a shortcut pressed in a hotkey box would toggle the overlay instead of getting there.
        _hotkeys?.UnregisterAll();
        bool saved = dlg.ShowDialog() == true;
        RegisterHotkeys();
        if (!saved) return;
        Loc.SetLanguage(_vm.Service.Settings.UiLanguage);
        ThemeManager.Apply(_vm.Service.Settings.Theme);
        UpdateTray();
        _vm.SettingsChanged();
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

    /// <summary>
    /// A note, group or own estimate is kept when its box loses focus, which switching to the game or closing the
    /// window does not do: what was typed would be lost.
    /// </summary>
    private void KeepTyped() =>
        (FocusManager.GetFocusedElement(this) as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

    protected override void OnDeactivated(EventArgs e)
    {
        KeepTyped();
        base.OnDeactivated(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        KeepTyped();
        _overlay?.SavePosition();
        if (!_quitting && _tray is not null)
        {
            // Into the notification area: the app keeps watching for the game, the hotkeys and the overlay work on.
            e.Cancel = true;
            Hide();
            HintTrayOnce();
            base.OnClosing(e);
            return;
        }
        // Unregister the hotkeys while the window is alive (UnregisterHotKey fails once it is destroyed).
        _hotkeys?.Dispose();
        _hotkeys = null;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _overlay?.Close();
        _vm.Dispose();
        base.OnClosed(e);
    }
}
