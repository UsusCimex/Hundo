using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace AC.App;

/// <summary>
/// The app's icon in the notification area through Shell_NotifyIcon: a click opens the window, a right click shows the
/// menu. Comes back by itself when Explorer restarts.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001; // WM_APP + 1
    private const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_MODIFY = 1;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const int WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly uint _taskbarCreated;
    private readonly string _tip;
    private readonly ContextMenu _menu;

    public event Action? Click;

    public TrayIcon(string tip, ContextMenu menu)
    {
        _tip = tip;
        _menu = menu;
        _menu.Placement = PlacementMode.MousePoint;
        // A hidden top-level window: Explorer tells only those that the taskbar is back.
        _window = new HwndSource(new HwndSourceParameters("HundoTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _window.AddHook(WndProc);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        ExtractIconEx(Environment.ProcessPath ?? "", 0, IntPtr.Zero, out _icon, 1);
        Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP);
    }

    /// <summary>A notification from the icon (a toast on Windows 10 and 11).</summary>
    public void ShowInfo(string title, string text) => Notify(NIM_MODIFY, NIF_INFO, title, text);

    private void Notify(uint message, int flags, string infoTitle = "", string info = "")
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tip,
            szInfo = info,
            szInfoTitle = infoTitle,
            dwInfoFlags = 1, // NIIF_INFO
        };
        Shell_NotifyIcon(message, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)lParam)
            {
                case WM_LBUTTONUP:
                    Click?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    // In front, so that a click anywhere else closes the menu.
                    SetForegroundWindow(hwnd);
                    _menu.IsOpen = true;
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated)
        {
            Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Notify(NIM_DELETE, 0);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
