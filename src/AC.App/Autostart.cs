using Microsoft.Win32;

namespace AC.App;

/// <summary>Start with Windows: a value under the user's Run key that starts this exe in the tray.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Hundo";

    private static string Command => $"\"{Environment.ProcessPath}\" {App.TrayArg}";

    /// <summary>On for this copy of the app: another copy's value (another folder) does not count.</summary>
    public static bool IsOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(Name, Command);
        else if (IsOn) key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
