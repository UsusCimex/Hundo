using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace AC.App;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>
    /// Lets any process bring its window to the foreground. The second app instance calls it
    /// (the user started it, so it is allowed to) so that the first one can show its window on top.
    /// </summary>
    public static void AllowAnyForeground() => AllowSetForegroundWindow(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>The work area of the screen the window is on (the nearest one), in pixels.</summary>
    public static RECT? WorkAreaOf(IntPtr hwnd)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info) ? info.Work : null;
    }

    public static void SetExStyle(IntPtr hwnd, int add, int remove)
    {
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = (style | (uint)add) & ~(long)(uint)remove;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }
}

/// <summary>Global hotkeys through RegisterHotKey: they work even when the game has focus.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xA000;

    public HotkeyManager(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("no HwndSource");
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a shortcut like "Ctrl+Shift+A". Returns false if the string is bad or the shortcut is taken.</summary>
    public bool Register(string gesture, Action action)
    {
        if (!TryParse(gesture, out var mods, out var vk)) return false;
        int id = _nextId++;
        if (!Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _nextId = 0xA000;
    }

    public static bool TryParse(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts[..^1])
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "win": modifiers |= Native.MOD_WIN; break;
                default: return false;
            }
        }
        var keyName = parts[^1];
        if (keyName.Length == 1 && char.IsDigit(keyName[0])) keyName = "D" + keyName;
        if (!Enum.TryParse<Key>(keyName, ignoreCase: true, out var key) || key == Key.None) return false;
        // Without a modifier the key is lost to the game system-wide, so only F1-F24 may go bare.
        if (modifiers == 0 && key is not (>= Key.F1 and <= Key.F24)) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
