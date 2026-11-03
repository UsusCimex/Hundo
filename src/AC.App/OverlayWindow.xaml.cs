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
        main.Overlay.Missable.CollectionChanged += OnMissableChanged;
    }

    private void OnMissableChanged(object? sender, NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(HasMissable));

    public void Detach() => Main.Overlay.Missable.CollectionChanged -= OnMissableChanged;

    public MainViewModel Main { get; }

    public bool HasMissable => Main.Overlay.Missable.Count > 0;

    private string _toast = "";
    public string Toast { get => _toast; set { if (Set(ref _toast, value)) OnPropertyChanged(nameof(HasToast)); } }
    public bool HasToast => Toast.Length > 0;

    private bool _isInteractive;
    public bool IsInteractive { get => _isInteractive; set { if (Set(ref _isInteractive, value)) RefreshHints(); } }

    /// <summary>The "Done" button on items: only in click mode and when marks are enabled.</summary>
    public bool ShowActions => IsInteractive && Main.ManualUnlocks;

    public string ModeHint
    {
        get
        {
            var s = Main.Services.Settings;
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
    // The lists keep room for a few items even when the overlay is dragged low.
    private const double MinRoom = 160;
    private readonly OverlayViewModel _vm;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private IntPtr _hwnd;

    public OverlayWindow(MainViewModel main)
    {
        InitializeComponent();
        _vm = new OverlayViewModel(main);
        DataContext = _vm;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _vm.Toast = "";
            ToastEnded?.Invoke();
        };
        Closed += (_, _) => { _toastTimer.Stop(); _vm.Detach(); };
        LocationChanged += (_, _) => FitHeight();
        DpiChanged += (_, _) => FitHeight();
        // A screen may have gone since it was last shown.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) KeepOnScreen(); };
        var s = main.Services.Settings;
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
        FitHeight();
    }

    public void ApplySettings()
    {
        var s = _vm.Main.Services.Settings;
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

    public event Action? ToastEnded;

    public void ShowToast(string text)
    {
        _vm.Toast = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void SavePosition()
    {
        var s = _vm.Main.Services.Settings;
        s.OverlayLeft = Left;
        s.OverlayTop = Top;
        _vm.Main.Services.SaveSettings();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_vm.IsInteractive) Native.SetExStyle(_hwnd, 0, Native.WS_EX_TRANSPARENT);
        else Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED, 0);
    }

    /// <summary>
    /// If the top-left corner it is dragged by is on no screen (a monitor was unplugged, or the position falls between
    /// screens of different sizes), moves it to the corner of the main screen.
    /// </summary>
    private void KeepOnScreen()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var rect)) return;
        if (Native.OnAScreen(new Native.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Left + 50, Bottom = rect.Top + 50 })) return;
        Left = SystemParameters.WorkArea.Left + 40;
        Top = SystemParameters.WorkArea.Top + 120;
    }

    /// <summary>
    /// No taller than the screen below its top: with up to 20 items, taller still in click mode, it ran off the screen.
    /// The lists scroll instead.
    /// </summary>
    private void FitHeight()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var rect) || Native.WorkAreaOf(_hwnd) is not { } work) return;
        double room = (work.Bottom - Math.Max(rect.Top, work.Top)) / VisualTreeHelper.GetDpi(this).DpiScaleY;
        MaxHeight = Math.Max(MinRoom, room);
    }

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.IsInteractive && e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }
}
