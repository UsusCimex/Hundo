using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AC.Core;

namespace AC.Infrastructure;

public sealed class SteamClientException(string message) : Exception(message);

/// <summary>
/// A connection to the running Steam client as a game. Loads steamclient64.dll from the Steam folder and calls its
/// C++ interfaces through their virtual tables. Steam takes the game from the SteamAppId environment variable, so a
/// process can act as one game only, and while it is connected Steam shows the user as playing that game.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class SteamClientSession : IDisposable
{
    // Versions with a known table layout; Steam keeps old versions working for old games.
    private const string ClientVersion = "SteamClient018";
    private const string UserVersion = "SteamUser012";
    private const string UserStatsVersion = "STEAMUSERSTATS_INTERFACE_VERSION013";
    private const string AppsVersion = "STEAMAPPS_INTERFACE_VERSION008";
    private const int UserStatsReceived = 1101;
    private const int UserStatsStored = 1102;
    private const uint LoadWithAlteredSearchPath = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct CallbackMessage
    {
        public int User;
        public int Id;
        public IntPtr Param;
        public int ParamSize;
    }

    private readonly IntPtr _client;
    private readonly int _pipe;
    private readonly int _user;
    private readonly IntPtr _steamUser;
    private readonly IntPtr _userStats;
    private readonly IntPtr _apps;
    private readonly delegate* unmanaged<int, CallbackMessage*, int*, byte> _getCallback;
    private readonly delegate* unmanaged<int, byte> _freeCallback;

    public SteamClientSession(uint appId)
    {
        var steam = SteamLocal.SteamPath() ?? throw new SteamClientException(Loc.T("client.notInstalled"));
        Environment.SetEnvironmentVariable("SteamAppId", appId.ToString(CultureInfo.InvariantCulture));
        // The altered search path lets the DLL find its neighbours (tier0, vstdlib) in the Steam folder.
        var dll = Path.Combine(steam, "steamclient64.dll");
        var library = LoadLibraryExW(dll, IntPtr.Zero, LoadWithAlteredSearchPath);
        if (library == IntPtr.Zero) throw new SteamClientException(Loc.T("client.loadFailed", dll));

        var create = (delegate* unmanaged<byte*, int*, IntPtr>)NativeLibrary.GetExport(library, "CreateInterface");
        _getCallback = (delegate* unmanaged<int, CallbackMessage*, int*, byte>)NativeLibrary.GetExport(library, "Steam_BGetCallback");
        _freeCallback = (delegate* unmanaged<int, byte>)NativeLibrary.GetExport(library, "Steam_FreeLastCallback");

        fixed (byte* version = Utf8(ClientVersion)) _client = create(version, null);
        if (_client == IntPtr.Zero) throw new SteamClientException(Loc.T("client.noInterface", ClientVersion));

        _pipe = ((delegate* unmanaged<IntPtr, int>)Slot(_client, 0))(_client);
        _user = _pipe == 0 ? 0 : ((delegate* unmanaged<IntPtr, int, int>)Slot(_client, 2))(_client, _pipe);
        if (_user == 0) throw new SteamClientException(Loc.T("client.notRunning"));

        _steamUser = Interface(UserVersion);
        _userStats = Interface(UserStatsVersion);
        _apps = Interface(AppsVersion);
    }

    public ulong SteamId
    {
        get
        {
            // A class returned by value comes back through a hidden pointer that follows "this".
            ulong id = 0;
            ((delegate* unmanaged<IntPtr, ulong*, ulong*>)Slot(_steamUser, 2))(_steamUser, &id);
            return id;
        }
    }

    public bool OwnsApp(uint appId) => ((delegate* unmanaged<IntPtr, uint, byte>)Slot(_apps, 6))(_apps, appId) != 0;

    public string GameLanguage => Marshal.PtrToStringUTF8(((delegate* unmanaged<IntPtr, IntPtr>)Slot(_apps, 4))(_apps)) ?? "";

    /// <summary>Asks Steam for the user's stats; the answer comes as a callback, see <see cref="WaitForUserStats"/>.</summary>
    public bool RequestUserStats(ulong steamId) =>
        ((delegate* unmanaged<IntPtr, ulong, ulong>)Slot(_userStats, 15))(_userStats, steamId) != 0;

    /// <summary>Steam's result code for the requested stats (1 is OK), or null if nothing came in time.</summary>
    public int? WaitForUserStats(uint appId, TimeSpan timeout) => WaitForResult(UserStatsReceived, appId, timeout);

    /// <summary>
    /// Steam's result code for the stats sent by <see cref="StoreStats"/> (1 is OK: stored), or null if nothing came in
    /// time.
    /// </summary>
    public int? WaitForStoredStats(uint appId, TimeSpan timeout) => WaitForResult(UserStatsStored, appId, timeout);

    private int? WaitForResult(int callback, uint appId, TimeSpan timeout)
    {
        int? result = null;
        var sw = Stopwatch.StartNew();
        while (result is null && sw.Elapsed < timeout)
        {
            // The game id (8 bytes) and the result (4 bytes) open both messages; the user id after them moves with packing.
            Drain(m =>
            {
                if (m.Id == callback && m.ParamSize >= 12 && (ulong)Marshal.ReadInt64(m.Param) == appId)
                    result = Marshal.ReadInt32(m.Param, 8);
            });
            if (result is null) Thread.Sleep(50);
        }
        return result;
    }

    public bool TryGetAchievement(string name, out bool achieved, out DateTimeOffset? unlockTime)
    {
        byte got;
        uint time;
        bool ok;
        fixed (byte* n = Utf8(name))
            ok = ((delegate* unmanaged<IntPtr, byte*, byte*, uint*, byte>)Slot(_userStats, 8))(_userStats, n, &got, &time) != 0;
        achieved = ok && got != 0;
        unlockTime = achieved && time > 0 ? DateTimeOffset.FromUnixTimeSeconds(time) : null;
        return ok;
    }

    public bool SetAchievement(string name, bool unlocked)
    {
        fixed (byte* n = Utf8(name))
            return ((delegate* unmanaged<IntPtr, byte*, byte>)Slot(_userStats, unlocked ? 6 : 7))(_userStats, n) != 0;
    }

    /// <summary>Sends the changes; true - Steam took the request, its answer comes later (<see cref="WaitForStoredStats"/>).</summary>
    public bool StoreStats() => ((delegate* unmanaged<IntPtr, byte>)Slot(_userStats, 9))(_userStats) != 0;

    /// <summary>Gives the client time to send stored stats before the process exits.</summary>
    public void Flush(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            Drain(_ => { });
            Thread.Sleep(50);
        }
    }

    public void Dispose()
    {
        ((delegate* unmanaged<IntPtr, int, int, void>)Slot(_client, 4))(_client, _pipe, _user);
        ((delegate* unmanaged<IntPtr, int, byte>)Slot(_client, 1))(_client, _pipe);
    }

    private IntPtr Interface(string version)
    {
        IntPtr result;
        fixed (byte* v = Utf8(version))
            result = ((delegate* unmanaged<IntPtr, int, int, byte*, IntPtr>)Slot(_client, 12))(_client, _user, _pipe, v);
        return result != IntPtr.Zero ? result : throw new SteamClientException(Loc.T("client.noInterface", version));
    }

    private void Drain(Action<CallbackMessage> handle)
    {
        CallbackMessage message;
        int call;
        while (_getCallback(_pipe, &message, &call) != 0)
        {
            try { handle(message); }
            finally { _freeCallback(_pipe); }
        }
    }

    private static IntPtr Slot(IntPtr instance, int index) => (*(IntPtr**)instance)[index];

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
}
