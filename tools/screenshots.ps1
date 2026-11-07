<#
.SYNOPSIS
    Screenshots of the app windows for UI checks and the README.
.DESCRIPTION
    Works on copies, on a desktop of its own: the app's windows never show on the screen and never take the mouse, the
    keyboard focus or the foreground. The build runs with the Steam client turned off (AC_NO_STEAM_CLIENT, so it
    cannot show you in-game or write achievements), the data folder goes to a temp folder (the original is untouched).
    The data folder needs settings.json and games\<appid>.json; the game from LastAppId opens. Takes the main window
    (sorted by plan), the time panel, the list sorted by date with the chart, the overlay over the main window and the
    overlay alone on a transparent background.
    -Set overrides settings in the copy: "Key=value;Key=value", the value is JSON or a string.
.EXAMPLE
    dotnet build -c Release
    .\tools\screenshots.ps1 -DataDir D:\ac-demo -OutDir docs\images -Theme Dark -Set "UiLanguage=en;ManualUnlocks=true"
#>
param(
    [Parameter(Mandatory)] [string] $DataDir,
    [Parameter(Mandatory)] [string] $OutDir,
    [ValidateSet("Light", "Dark")] [string] $Theme = "Dark",
    [string] $Set = "",
    [string] $Build,
    [int] $SelectIndex = 1
)
$ErrorActionPreference = "Stop"
# Not in param(): Windows PowerShell 5.1 leaves $PSScriptRoot empty there when run with -File.
if (-not $Build) { $Build = Join-Path $PSScriptRoot "..\src\AC.App\bin\Release\net10.0-windows" }

$running = (Get-ItemProperty "HKCU:\Software\Valve\Steam" -Name RunningAppID -ErrorAction SilentlyContinue).RunningAppID
if ($running -and $running -ne 0) { throw "A game is running (AppID $running): not opening windows over it" }

# The script starts itself again on a desktop of its own and prints what it printed there.
if ($env:AC_SHOTS_DESKTOP -ne "1") {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class ShotsDesktop {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    // The exit code, or -1 when the process ran out of time and was ended.
    public static int Run(string desktop, string cmd, uint timeoutMs) {
        IntPtr d = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, 0x10000000u, IntPtr.Zero);
        if (d == IntPtr.Zero) throw new Win32Exception();
        try {
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            si.lpDesktop = "WinSta0\\" + desktop;
            PROCESS_INFORMATION pi;
            if (!CreateProcess(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, 0x08000000u, IntPtr.Zero, null, ref si, out pi))
                throw new Win32Exception();
            try {
                if (WaitForSingleObject(pi.hProcess, timeoutMs) != 0) { TerminateProcess(pi.hProcess, 1); return -1; }
                uint code;
                GetExitCodeProcess(pi.hProcess, out code);
                return (int)code;
            } finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        } finally { CloseDesktop(d); }
    }
}
"@
    function Quote([string] $s) { "'" + $s.Replace("'", "''") + "'" }
    function Full([string] $path) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path) }
    $out = [IO.Path]::GetTempFileName()
    $call = "& $(Quote $PSCommandPath) -DataDir $(Quote (Full $DataDir)) -OutDir $(Quote (Full $OutDir)) -Theme $Theme " +
        "-Set $(Quote $Set) -Build $(Quote (Full $Build)) -SelectIndex $SelectIndex"
    $body = "`$env:AC_SHOTS_DESKTOP = '1'`n" +
        "try { $call *>&1 | Out-File -LiteralPath $(Quote $out) -Encoding utf8 -Width 500; exit 0 }`n" +
        "catch { `$_ | Out-File -LiteralPath $(Quote $out) -Append -Encoding utf8 -Width 500; exit 1 }"
    $shell = (Get-Process -Id $PID).Path
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
    try {
        $code = [ShotsDesktop]::Run("HundoShots", "`"$shell`" -NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded", 600000)
        Get-Content $out -Encoding UTF8
    }
    finally { Remove-Item $out -ErrorAction SilentlyContinue }
    if ($code -eq -1) {
        # Its own cleanup did not run: end the copy it left running.
        Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -like (Join-Path ([IO.Path]::GetTempPath()) "ac-shots-*") } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        throw "Timed out"
    }
    if ($code -ne 0) { throw "Failed: exit code $code" }
    return
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public static class Shot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    // The app's visible window that no other owns: Windows also gives the process an input indicator window here.
    public static IntPtr MainWindow(int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || GetWindow(h, 4) != IntPtr.Zero || !IsWindowVisible(h)) return true;
            var c = new StringBuilder(256);
            GetClassName(h, c, 256);
            if (!c.ToString().StartsWith("HwndWrapper")) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
    // The window as it draws itself, images included, cut to its visible frame: no screen is needed.
    public static Bitmap Grab(IntPtr h) {
        RECT w, v;
        GetWindowRect(h, out w);
        if (DwmGetWindowAttribute(h, 9, out v, Marshal.SizeOf(typeof(RECT))) != 0) v = w;
        using (var full = new Bitmap(w.R - w.L, w.B - w.T, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(full)) {
                IntPtr dc = g.GetHdc();
                PrintWindow(h, dc, 2);
                g.ReleaseHdc(dc);
            }
            return full.Clone(new Rectangle(v.L - w.L, v.T - w.T, v.R - v.L, v.B - v.T), PixelFormat.Format32bppArgb);
        }
    }
    public static void Save(IntPtr h, string path) { using (var b = Grab(h)) b.Save(path, ImageFormat.Png); }
    // The overlay's own picture over the window: a shot of the overlay has no transparency.
    public static void SaveUnder(IntPtr h, string overlay, int x, int y, string path) {
        using (var b = Grab(h))
        using (var o = new Bitmap(overlay)) {
            using (var g = Graphics.FromImage(b)) g.DrawImage(o, x, y, o.Width, o.Height);
            b.Save(path, ImageFormat.Png);
        }
    }
}
"@
[void][Shot]::SetThreadDpiAwarenessContext([IntPtr](-4))
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function WaitFor([scriptblock] $check, [int] $seconds, [string] $what) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $seconds) { $r = & $check; if ($r) { return $r }; Start-Sleep -Milliseconds 300 }
    throw "Timed out waiting for $what"
}
function ById($root, $id) { $root.FindFirst($TS::Descendants, (Cond $AE::AutomationIdProperty $id)) }
function Toggle($root, $id) { (ById $root $id).GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
function SelectOption($root, $id, [int] $index) {
    $box = ById $root $id
    $box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $options = WaitFor { $o = $box.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::ListItem)); if ($o.Count -gt $index) { , $o } } 10 "the options of $id"
    $options[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ac-shots-" + [Guid]::NewGuid().ToString("N"))
$app = Join-Path $tmp "app"
$data = Join-Path $tmp "data"
$overlayShot = Join-Path $tmp "overlay.png"
$OutDir = (New-Item -ItemType Directory -Force $OutDir).FullName
Copy-Item $Build $app -Recurse
# A build without these switches would open the user's real data folder or connect to Steam.
$latin = [Text.Encoding]::GetEncoding(28591)
$infra = $latin.GetString([IO.File]::ReadAllBytes((Join-Path $app "AC.Infrastructure.dll")))
foreach ($switch in "AC_DATA_DIR", "AC_NO_STEAM_CLIENT") {
    if (-not $infra.Contains($latin.GetString([Text.Encoding]::Unicode.GetBytes($switch)))) { throw "The build does not support $switch - not starting it" }
}
Copy-Item $DataDir $data -Recurse
$settingsPath = Join-Path $data "settings.json"
$settings = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$settings | Add-Member -NotePropertyName Theme -NotePropertyValue $Theme -Force
# Closed to the tray, the copy would stay running and leave an icon.
$settings | Add-Member -NotePropertyName CloseToTray -NotePropertyValue $false -Force
foreach ($pair in ($Set -split ";" | Where-Object { $_ })) {
    $key, $value = $pair -split "=", 2
    try { $value = $value | ConvertFrom-Json } catch { }
    $settings | Add-Member -NotePropertyName $key.Trim() -NotePropertyValue $value -Force
}
[IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5))

$psi = New-Object Diagnostics.ProcessStartInfo (Join-Path $app "Hundo.exe")
$psi.UseShellExecute = $false
$psi.EnvironmentVariables["AC_DATA_DIR"] = $data
$psi.EnvironmentVariables["AC_NO_STEAM_CLIENT"] = "1"
# Hardware rendering stops while the display is off, software rendering does not.
$psi.EnvironmentVariables["AC_SOFTWARE_RENDERING"] = "1"
# The build does not check the markup's bindings: their errors are printed at the end.
$psi.EnvironmentVariables["AC_BINDING_ERRORS"] = "1"
$psi.EnvironmentVariables["AC_OVERLAY_SHOT"] = $overlayShot
$p = [Diagnostics.Process]::Start($psi)
$suffix = $Theme.ToLowerInvariant()
try {
    $hwnd = WaitFor { [Shot]::MainWindow($p.Id) | Where-Object { $_ -ne [IntPtr]::Zero } } 30 "the main window"
    $win = $AE::FromHandle($hwnd)
    $list = WaitFor { ById $win "AchievementList" } 30 "the list"
    # Sorted by plan, the list is grouped under headings: the cards are deeper than its children. The list is built
    # again while the game loads, and the cards found before take no selection.
    WaitFor {
        $i = $list.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::ListItem))
        if ($i.Count -gt $SelectIndex) {
            try { $i[$SelectIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); $true }
            catch { $false }
        }
    } 60 "an achievement to select" | Out-Null
    Start-Sleep -Seconds 4
    [Shot]::Save($hwnd, (Join-Path $OutDir "main-$suffix.png"))

    Toggle $win "TimeTile"
    Start-Sleep -Seconds 2
    [Shot]::Save($hwnd, (Join-Path $OutDir "time-$suffix.png"))
    Toggle $win "TimeTile"

    # The fifth sort is by unlock date.
    SelectOption $win "SortBox" 4
    Start-Sleep -Seconds 3
    [Shot]::Save($hwnd, (Join-Path $OutDir "history-$suffix.png"))
    SelectOption $win "SortBox" 0
    (ById $win "OverlayButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $shown = Get-Date
    # The app saves the overlay every second while it is shown; the first ones may come before its images.
    WaitFor { (Test-Path $overlayShot) -and (Get-Item $overlayShot).LastWriteTime -gt $shown.AddSeconds(3) } 20 "the overlay" | Out-Null
    Copy-Item $overlayShot (Join-Path $OutDir "overlay-alone-$suffix.png")
    [Shot]::SaveUnder($hwnd, (Join-Path $OutDir "overlay-alone-$suffix.png"), 330, 330, (Join-Path $OutDir "overlay-$suffix.png"))
    "Done: $OutDir"
}
finally {
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(8000)) { $p.Kill() } }
    Start-Sleep -Milliseconds 500
    Get-Content (Join-Path $data "app.log") -Encoding UTF8 -ErrorAction SilentlyContinue | Where-Object { $_ -like "*Binding: *" } |
        ForEach-Object { "WARNING: $_" }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
