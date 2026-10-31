using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AC.Core;

namespace AC.App;

public sealed class OverlayViewModel : ObservableObject
{
    public OverlayViewModel(MainViewModel main)
    {
        Main = main;
        main.OverlayMissable.CollectionChanged += OnMissableChanged;
    }

    private void OnMissableChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(HasMissable));

    public void Detach() => Main.OverlayMissable.CollectionChanged -= OnMissableChanged;

    public MainViewModel Main { get; }

    public bool HasMissable => Main.OverlayMissable.Count > 0;

    private string _toast = "";
    public string Toast { get => _toast; set { if (Set(ref _toast, value)) OnPropertyChanged(nameof(HasToast)); } }
    public bool HasToast => Toast.Length > 0;

    private bool _isInteractive;
    public bool IsInteractive { get => _isInteractive; set { if (Set(ref _isInteractive, value)) RefreshHints(); } }

    /// <summary>"Done" and "To Steam" buttons on items: only in click mode and when marks are enabled.</summary>
    public bool ShowActions => IsInteractive && Main.ManualUnlocks;

    public string ModeHint
    {
        get
        {
            var s = Main.Service.Settings;
            return IsInteractive
                ? Loc.T(Main.ManualUnlocks ? "overlay.interactMark" : "overlay.interactDrag", s.OverlayInteractHotkey)
                : Loc.T("overlay.keys", s.OverlayToggleHotkey, s.OverlayInteractHotkey);
        }
    }

    public void RefreshHints()
    {
        OnPropertyChanged(nameof(ModeHint));
        OnPropertyChanged(nameof(ShowActions));
    }
}

/// <summary>
/// The overlay is a plain transparent topmost window.
/// Injects nothing; visible in windowed and borderless modes.
/// In view mode clicks pass through the window (WS_EX_TRANSPARENT).
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private IntPtr _hwnd;

    public OverlayWindow(MainViewModel main)
    {
        InitializeComponent();
        _vm = new OverlayViewModel(main);
        DataContext = _vm;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _vm.Toast = ""; };
        Closed += (_, _) => { _toastTimer.Stop(); _vm.Detach(); };
        var s = main.Service.Settings;
        Left = s.OverlayLeft;
        Top = s.OverlayTop;
        ApplySettings();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, 0);
        ApplyClickThrough();
        KeepOnScreen();
    }

    public void ApplySettings()
    {
        var s = _vm.Main.Service.Settings;
        var opacity = Math.Clamp(s.OverlayOpacity, 0.2, 1.0);
        OverlayPanel.Background = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), 0x14, 0x1A, 0x23));
        _vm.RefreshHints();
    }

    public void ToggleInteractive()
    {
        if (!IsVisible) Show();
        _vm.IsInteractive = !_vm.IsInteractive;
        ApplyClickThrough();
        if (!_vm.IsInteractive) SavePosition();
    }

    /// <summary>Hides it out of click mode: shown again, it would still catch the clicks meant for the game.</summary>
    public void HideToGame()
    {
        if (_vm.IsInteractive) ToggleInteractive();
        Hide();
    }

    public void ShowToast(string text)
    {
        _vm.Toast = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void SavePosition()
    {
        var s = _vm.Main.Service.Settings;
        s.OverlayLeft = Left;
        s.OverlayTop = Top;
        _vm.Main.Service.SaveSettings();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_vm.IsInteractive) Native.SetExStyle(_hwnd, 0, Native.WS_EX_TRANSPARENT);
        else Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED, 0);
    }

    /// <summary>If the saved position is off all screens (the monitor changed), moves it back to the corner.</summary>
    private void KeepOnScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;
        if (Left < left || Left > right - 50 || Top < top || Top > bottom - 50)
        {
            Left = left + 40;
            Top = top + 120;
        }
    }

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsInteractive && e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }
}
