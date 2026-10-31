using System.Windows;

namespace AC.App;

/// <summary>
/// Windows no larger than the screen's work area: the default sizes do not fit a laptop screen at 125-150% scaling,
/// and a centered window taller than the screen has its title bar and buttons off screen.
/// </summary>
internal static class WindowFit
{
    public static void ToWorkArea(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth, area.Width);
        window.MinHeight = Math.Min(window.MinHeight, area.Height);
        window.Width = Math.Min(window.Width, area.Width);
        window.Height = Math.Min(window.Height, area.Height);
    }
}
