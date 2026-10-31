using AC.Core;

namespace AC.Infrastructure;

/// <summary>What to do in Steam on a manual mark.</summary>
public enum WriteMode
{
    /// <summary>Mark in the app only, leave Steam alone.</summary>
    MarkOnly = 0,
    /// <summary>Mark and write to Steam now via the helper.</summary>
    MarkAndUnlock = 1,
    /// <summary>Marks are queued and sent to Steam by a button.</summary>
    Queue = 2,
}

/// <summary>Where auto refresh gets the unlocked status from.</summary>
public enum StatusSource
{
    /// <summary>Steam Web API: needs a key and public "Game details".</summary>
    WebApi = 0,
    /// <summary>Via the Steam client (helper): works with private profiles, shows "in game" for a second.</summary>
    SteamClient = 1,
}

/// <summary>What the hours of an achievement show.</summary>
public enum HoursMode
{
    /// <summary>When it usually comes, counted from the start of the game.</summary>
    FromStart = 0,
    /// <summary>How much is left to it from the player's progress.</summary>
    Left = 1,
}

/// <summary>Order of the library list.</summary>
public enum LibrarySort
{
    /// <summary>As Steam gives it: recently played first.</summary>
    Recent = 0,
    /// <summary>Closest to 100% first.</summary>
    TimeLeft = 1,
}

/// <summary>Color theme.</summary>
public enum AppTheme
{
    /// <summary>As in Windows (follows system changes).</summary>
    System = 0,
    Light = 1,
    Dark = 2,
}

public sealed class AppSettings
{
    /// <summary>1 - the hotkeys moved off Ctrl+Shift+A and S.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Settings saved by an older version are brought up to date once, see <see cref="Upgrade"/>.</summary>
    public int Format { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>API key encrypted with DPAPI (base64). Never stored as plain text.</summary>
    public string ApiKeyProtected { get; set; } = "";
    public ulong SteamId64 { get; set; }
    /// <summary>Interface language code (en, ru...); empty means the Windows language.</summary>
    public string UiLanguage { get; set; } = Loc.Fallback;
    /// <summary>Steam language of achievement names and descriptions (english, russian...); empty means the interface language.</summary>
    public string Language { get; set; } = "";
    public Percentile Percentile { get; set; } = Percentile.P50;
    public HoursMode Hours { get; set; } = HoursMode.FromStart;
    public LibrarySort LibrarySort { get; set; } = LibrarySort.Recent;
    /// <summary>The library lists only games with something unlocked and not at 100%.</summary>
    public bool LibraryStartedOnly { get; set; }
    /// <summary>
    /// Manual "done but not counted" marks and writes to Steam.
    /// Off - progress counts only achievements earned in the game.
    /// </summary>
    public bool ManualUnlocks { get; set; }
    public WriteMode WriteMode { get; set; } = WriteMode.MarkOnly;
    /// <summary>While the open game runs, the achievement whose time is up is marked as by the "Done" checkbox.</summary>
    public bool AutoMark { get; set; }
    public bool HideSpoilers { get; set; } = true;
    public char CsvSeparator { get; set; } = ';';

    public string OverlayToggleHotkey { get; set; } = "Ctrl+Alt+H";
    public string OverlayInteractHotkey { get; set; } = "Ctrl+Alt+J";
    public double OverlayLeft { get; set; } = 40;
    public double OverlayTop { get; set; } = 120;
    public double OverlayOpacity { get; set; } = 0.85;
    public int OverlayNearestCount { get; set; } = 5;
    /// <summary>The overlay shows itself when the open game starts and for a message, and hides when the game closes.</summary>
    public bool OverlayAuto { get; set; } = true;

    /// <summary>How often to poll the Web API while playing, seconds (if the stats file is unchanged).</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Status source for auto refresh (on stats file changes and on the timer).</summary>
    public StatusSource StatusSource { get; set; } = StatusSource.WebApi;

    public uint LastAppId { get; set; }

    /// <summary>Look for a new release on GitHub at startup.</summary>
    public bool CheckUpdates { get; set; } = true;

    /// <summary>Brings settings of an older version up to date. True - they changed and are worth saving.</summary>
    public bool Upgrade()
    {
        if (Format >= CurrentFormat) return false;
        // The old defaults are crouch, sprint and strafe in many shooters: the overlay toggled mid-game.
        if (OverlayToggleHotkey == "Ctrl+Shift+A") OverlayToggleHotkey = "Ctrl+Alt+H";
        if (OverlayInteractHotkey == "Ctrl+Shift+S") OverlayInteractHotkey = "Ctrl+Alt+J";
        Format = CurrentFormat;
        return true;
    }
}
