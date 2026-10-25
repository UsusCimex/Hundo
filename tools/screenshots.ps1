<#
.SYNOPSIS
    Screenshots of the app windows for UI checks and the README.
.DESCRIPTION
    Works on copies: the build runs with the Steam client turned off (AC_NO_STEAM_CLIENT, so it cannot show you
    in-game or write achievements), the data folder goes to a temp folder (the original is untouched). The data folder needs settings.json and
    games\<appid>.json; the game from LastAppId opens. Takes the main window (sorted by plan), the time panel, the list
    sorted by date with the chart, the overlay over the main window and the overlay alone on a transparent background.
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

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class Shot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    public static RECT Bounds(IntPtr h) { RECT v; DwmGetWindowAttribute(h, 9, out v, Marshal.SizeOf(typeof(RECT))); return v; }
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    // A window started from a hidden console, or minimized meanwhile, is restored without taking the focus.
    public static void TopMost(IntPtr h) { if (IsIconic(h)) ShowWindow(h, 4); SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }
    public static void Move(IntPtr h, int x, int y) { SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010); }
    public static void Grab(IntPtr h, string path) { GrabRect(Bounds(h), path); }
    // From the screen, not PrintWindow: that one misses images loaded after the first paint.
    // CAPTUREBLT so the overlay (a layered window) gets in.
    public static void GrabRect(RECT v, string path) {
        using (var bmp = new Bitmap(v.R - v.L, v.B - v.T, PixelFormat.Format32bppArgb)) {
            using (var g = Graphics.FromImage(bmp)) {
                IntPtr dst = g.GetHdc(); IntPtr src = GetDC(IntPtr.Zero);
                BitBlt(dst, 0, 0, bmp.Width, bmp.Height, src, v.L, v.T, 0x00CC0020 | 0x40000000);
                ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }
    // The same window shot over black and over white gives its own colors and transparency.
    public static void Matte(string onBlack, string onWhite, string path) {
        using (var b = new Bitmap(onBlack))
        using (var w = new Bitmap(onWhite))
        using (var o = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb)) {
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++) {
                    Color cb = b.GetPixel(x, y), cw = w.GetPixel(x, y);
                    int a = 255 - Clamp((cw.R - cb.R + cw.G - cb.G + cw.B - cb.B) / 3);
                    o.SetPixel(x, y, a == 0 ? Color.Transparent
                        : Color.FromArgb(a, Clamp(cb.R * 255 / a), Clamp(cb.G * 255 / a), Clamp(cb.B * 255 / a)));
                }
            o.Save(path, ImageFormat.Png);
        }
    }
    static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }
}
"@
[void][Shot]::SetThreadDpiAwarenessContext([IntPtr](-4))
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Both($a, $b) { New-Object System.Windows.Automation.AndCondition($a, $b) }
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
# A full path: Bitmap.Save resolves a relative path against the process folder, not PowerShell's current one.
$OutDir = (New-Item -ItemType Directory -Force $OutDir).FullName
Copy-Item (Resolve-Path $Build) $app -Recurse
# A build without these switches would open the user's real data folder or connect to Steam.
$latin = [Text.Encoding]::GetEncoding(28591)
$infra = $latin.GetString([IO.File]::ReadAllBytes((Join-Path $app "AC.Infrastructure.dll")))
foreach ($switch in "AC_DATA_DIR", "AC_NO_STEAM_CLIENT") {
    if (-not $infra.Contains($latin.GetString([Text.Encoding]::Unicode.GetBytes($switch)))) { throw "The build does not support $switch - not starting it" }
}
Copy-Item (Resolve-Path $DataDir) $data -Recurse
$settingsPath = Join-Path $data "settings.json"
$settings = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$settings | Add-Member -NotePropertyName Theme -NotePropertyValue $Theme -Force
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
$p = [Diagnostics.Process]::Start($psi)
$suffix = $Theme.ToLowerInvariant()
try {
    $hwnd = WaitFor { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $p.MainWindowHandle } } 30 "the main window"
    [Shot]::TopMost($hwnd)
    $win = $AE::FromHandle($hwnd)
    $list = WaitFor { ById $win "AchievementList" } 30 "the list"
    # Sorted by plan, the list is grouped under headings: the cards are deeper than its children.
    $items = WaitFor { $i = $list.FindAll($TS::Descendants, (Cond $AE::ControlTypeProperty $CT::ListItem)); if ($i.Count -gt $SelectIndex) { , $i } } 60 "achievements"
    $items[$SelectIndex].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 4
    # Other windows may have come up while waiting (the window is shot as it is on screen).
    [Shot]::TopMost($hwnd); Start-Sleep -Milliseconds 300
    [Shot]::Grab($hwnd, (Join-Path $OutDir "main-$suffix.png"))

    Toggle $win "TimeTile"
    Start-Sleep -Seconds 2
    [Shot]::TopMost($hwnd); Start-Sleep -Milliseconds 300
    [Shot]::Grab($hwnd, (Join-Path $OutDir "time-$suffix.png"))
    Toggle $win "TimeTile"

    # The fifth sort is by unlock date.
    SelectOption $win "SortBox" 4
    Start-Sleep -Seconds 3
    [Shot]::TopMost($hwnd); Start-Sleep -Milliseconds 300
    [Shot]::Grab($hwnd, (Join-Path $OutDir "history-$suffix.png"))
    SelectOption $win "SortBox" 0
    (ById $win "OverlayButton").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $overlay = WaitFor { $AE::RootElement.FindFirst($TS::Children, (Both (Cond $AE::ProcessIdProperty $p.Id) (Cond $AE::AutomationIdProperty "Overlay"))) } 15 "the overlay"
    $ovh = [IntPtr]$overlay.Current.NativeWindowHandle
    $b = [Shot]::Bounds($hwnd)
    # The overlay goes over the main window so nothing else gets into the shot.
    [Shot]::Move($ovh, $b.L + 330, $b.T + 330)
    Start-Sleep -Seconds 3
    [Shot]::Grab($hwnd, (Join-Path $OutDir "overlay-$suffix.png"))

    $ob = [Shot]::Bounds($ovh)
    $backdrop = New-Object System.Windows.Forms.Form
    $backdrop.FormBorderStyle = "None"; $backdrop.ShowInTaskbar = $false; $backdrop.StartPosition = "Manual"; $backdrop.AutoScaleMode = "None"
    $backdrop.TopMost = $true
    $backdrop.Bounds = New-Object System.Drawing.Rectangle(($ob.L - 20), ($ob.T - 20), ($ob.R - $ob.L + 40), ($ob.B - $ob.T + 40))
    $shots = @{}
    foreach ($color in "Black", "White") {
        $backdrop.BackColor = [System.Drawing.Color]::$color
        if (-not $backdrop.Visible) { $backdrop.Show() }
        $backdrop.Refresh(); [System.Windows.Forms.Application]::DoEvents()
        [Shot]::TopMost($ovh)
        Start-Sleep -Milliseconds 600
        $shots[$color] = Join-Path $tmp "overlay-$color.png"
        [Shot]::GrabRect($ob, $shots[$color])
    }
    $backdrop.Close()
    [Shot]::Matte($shots["Black"], $shots["White"], (Join-Path $OutDir "overlay-alone-$suffix.png"))
    "Done: $OutDir"
}
finally {
    if (-not $p.HasExited) { [void]$p.CloseMainWindow(); if (-not $p.WaitForExit(8000)) { $p.Kill() } }
    Start-Sleep -Milliseconds 500
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
