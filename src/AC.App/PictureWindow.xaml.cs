using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using AC.Core;

namespace AC.App;

/// <summary>Guide pictures, larger: the wheel zooms where the pointer is, a drag moves, the arrows page through.</summary>
public partial class PictureWindow : Window
{
    private const double MaxZoom = 8;
    private readonly IReadOnlyList<GuidePictureViewModel> _pictures;
    private GuidePictureViewModel _current;
    private Point? _grip;

    public PictureWindow(IReadOnlyList<GuidePictureViewModel> pictures, GuidePictureViewModel current, string title, Window owner)
    {
        InitializeComponent();
        _pictures = pictures;
        _current = current;
        Title = title;
        Owner = owner;
        Width = Math.Max(MinWidth, owner.ActualWidth * 0.85);
        Height = Math.Max(MinHeight, owner.ActualHeight * 0.85);
        Stage.SizeChanged += (_, _) => Fit();
        PropertyChangedEventHandler recount = (_, _) => Recount();
        foreach (var p in pictures) p.PropertyChanged += recount;
        Closed += (_, _) => { foreach (var p in pictures) p.PropertyChanged -= recount; };
        Display(current);
    }

    /// <summary>Thumbnails still loading join the paging once they show.</summary>
    private List<GuidePictureViewModel> Shown() => _pictures.Where(p => p.IsShown || p == _current).ToList();

    private async void Display(GuidePictureViewModel picture)
    {
        _current = picture;
        Recount();
        Picture.Source = null;
        Fit();
        Status.Text = Loc.T("picture.loading");
        var image = await ImageCache.GetAsync(picture.Url, 0);
        if (_current != picture) return;
        Picture.Source = image;
        Status.Text = image is null ? Loc.T("picture.failed") : "";
    }

    private void Recount()
    {
        var shown = Shown();
        Counter.Text = shown.Count > 1 ? $"{shown.IndexOf(_current) + 1} / {shown.Count}" : "";
    }

    private void Page(int step)
    {
        var shown = Shown();
        if (shown.Count > 1) Display(shown[(shown.IndexOf(_current) + step + shown.Count) % shown.Count]);
    }

    private void Fit()
    {
        Zoom.ScaleX = Zoom.ScaleY = 1;
        Shift.X = Shift.Y = 0;
        Stage.Cursor = null;
    }

    /// <summary>Zooms keeping the point under the pointer in place.</summary>
    private void ZoomAt(Point at, double factor)
    {
        double from = Zoom.ScaleX, to = Math.Clamp(from * factor, 1, MaxZoom);
        Zoom.ScaleX = Zoom.ScaleY = to;
        Move(at.X - (at.X - Shift.X) * to / from, at.Y - (at.Y - Shift.Y) * to / from);
        Stage.Cursor = to > 1 ? Cursors.SizeAll : null;
    }

    /// <summary>The zoomed picture still covers the stage: it cannot slide away.</summary>
    private void Move(double x, double y)
    {
        double scale = Zoom.ScaleX;
        Shift.X = Math.Clamp(x, Stage.ActualWidth * (1 - scale), 0);
        Shift.Y = Math.Clamp(y, Stage.ActualHeight * (1 - scale), 0);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e) => ZoomAt(e.GetPosition(Stage), e.Delta > 0 ? 1.25 : 1 / 1.25);

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (Zoom.ScaleX > 1) Fit();
            else ZoomAt(e.GetPosition(Stage), 2.5);
            return;
        }
        if (Zoom.ScaleX <= 1) return;
        _grip = e.GetPosition(Stage);
        Stage.CaptureMouse();
    }

    private void OnDrag(object sender, MouseEventArgs e)
    {
        if (_grip is not { } grip) return;
        var at = e.GetPosition(Stage);
        Move(Shift.X + at.X - grip.X, Shift.Y + at.Y - grip.Y);
        _grip = at;
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        _grip = null;
        Stage.ReleaseMouseCapture();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var center = new Point(Stage.ActualWidth / 2, Stage.ActualHeight / 2);
        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.Left: Page(-1); break;
            case Key.Right: Page(1); break;
            case Key.Add or Key.OemPlus: ZoomAt(center, 1.25); break;
            case Key.Subtract or Key.OemMinus: ZoomAt(center, 1 / 1.25); break;
            case Key.D0 or Key.NumPad0: Fit(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_current.Url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status.Text = Loc.T("status.browserFailed", ex.Message);
        }
    }
}
