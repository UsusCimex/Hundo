using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>
/// Watches the running games and the open game's stats file: reads the open game's status while it runs and when
/// Steam writes its stats, opens a game that starts, and keeps the sessions. Errors of these steps come back every time
/// (no network during play): each is logged once and shown in the status line, not in a message box over the game.
/// </summary>
public sealed class GameWatch : ObservableObject, IDisposable
{
    /// <summary>
    /// Steam names a game for the moment the app reads its status through the Steam client: a game counts as started
    /// once it has run for a while.
    /// </summary>
    private static readonly TimeSpan PlayingAfterRunning = TimeSpan.FromSeconds(15);

    private readonly MainViewModel _main;
    private readonly AppServices _svc;
    private readonly PlayWatch _play = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private StatsFileWatcher? _statsFile;
    /// <summary>When the status was last read, on a clock the system time does not move.</summary>
    private readonly Stopwatch _sinceRead = new();
    /// <summary>The last error of a step that runs by itself (<see cref="OnRepeatedError"/>).</summary>
    private string? _repeatedError;

    public GameWatch(MainViewModel main, AppServices svc)
    {
        _main = main;
        _svc = svc;
        _timer.Tick += async (_, _) =>
        {
            try
            {
                await LookAsync();
            }
            catch (Exception e)
            {
                OnRepeatedError("Running games", e);
            }
        };
    }

    public SessionTracker Sessions => _play.Sessions;

    /// <summary>The last session of a game, if the app saw it from its start and it was not dismissed.</summary>
    public PlaySession? LastSession(uint appId) => _play.LastSession(appId);

    public void Dismiss(uint appId) => _play.Dismiss(appId);

    /// <summary>The open game started (true, once it has run for a while) or closed (false).</summary>
    public event Action<bool>? PlayingChanged;

    public bool Playing { get; private set; }

    private bool _autoFollow = true;
    /// <summary>Switch to the game running in Steam automatically.</summary>
    public bool AutoFollow { get => _autoFollow; set => Set(ref _autoFollow, value); }

    /// <summary>Starts watching; the games running now.</summary>
    public async Task<IReadOnlySet<uint>> StartAsync()
    {
        var running = await _play.StartAsync();
        _timer.Start();
        return running;
    }

    /// <summary>The status was just read: a read by itself waits.</summary>
    public void StatusRead() => _sinceRead.Restart();

    /// <summary>Reads the status when Steam writes the stats file of the game, under the account in the settings.</summary>
    public void WatchStatsFile(GameData? game)
    {
        _statsFile?.Dispose();
        _statsFile = null;
        if (game is null || _svc.Settings.SteamId64 == 0) return;
        var w = new StatsFileWatcher(_svc.Settings.SteamId64, game.AppId);
        w.Changed += () => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Reading via the Steam client can rewrite the file itself - avoid a loop.
            if (_sinceRead.IsRunning && _sinceRead.Elapsed < TimeSpan.FromSeconds(10)) return;
            if (_svc.Settings.HasApiKey || _svc.Bridge.Exists) _ = ReadAsync();
        });
        _statsFile = w;
    }

    private async Task LookAsync()
    {
        if (await _play.LookAsync() is not { } running) return;

        if (_play.HasChanges)
        {
            // While something loads, do not act on them; they wait for the next look.
            if (_main.IsBusy) return;
            var changes = _play.TakeChanges();
            _main.Session.Update();
            if (_main.Game is { } played && changes.Stopped.Contains(played.AppId))
            {
                // Unlocks of the game's last moments may have come after the last look: read once more, and the
                // session's summary takes them in.
                if (_svc.Settings.HasApiKey || _svc.Bridge.Exists) _ = ReadAsync();
                if (_svc.Settings.HasApiKey)
                {
                    var session = _play.LastSession(played.AppId);
                    _ = _main.SafeAsync(() => AfterSessionAsync(played, session));
                }
            }
            if (AutoFollow && changes.Started.Count > 0 && (_main.Game is null || !running.Contains(_main.Game.AppId)))
            {
                var next = PlayWatch.Followed(changes.Started);
                await _main.SafeAsync(() => _main.LoadAsync(next, _main.Library.NameOf(next)));
                return;
            }
        }

        // While the game runs, read the status (in case the stats file did not change).
        bool openRunning = _main.Game is { } open && running.Contains(open.AppId);
        bool canRead = _svc.Settings.HasApiKey || _svc.Bridge.Exists;
        if (openRunning && canRead && !_main.IsBusy &&
            (!_sinceRead.IsRunning || _sinceRead.Elapsed > TimeSpan.FromSeconds(Math.Max(15, _svc.Settings.PollSeconds))))
        {
            _sinceRead.Restart();
            await ReadAsync();
        }
        _main.CountDown();
        _main.MarkDue();
        UpdatePlaying(running);
    }

    private void UpdatePlaying(IReadOnlySet<uint> running)
    {
        // Seen, not since a pause: after the computer sleeps the game is still the one being played.
        bool playing = _main.Game is { } game && running.Contains(game.AppId) &&
                       _play.Sessions.SeenFor(game.AppId, DateTimeOffset.Now) >= PlayingAfterRunning;
        if (playing == Playing) return;
        Playing = playing;
        PlayingChanged?.Invoke(playing);
    }

    /// <summary>Steam counts a session's playtime a little after it ends; the new point goes to the playtime history.</summary>
    /// <param name="session">The session if the app saw it from its start.</param>
    private async Task AfterSessionAsync(GameData game, PlaySession? session)
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        if (_main.Game != game || _main.IsBusy || _play.Sessions.IsRunning(game.AppId)) return;
        try
        {
            await _svc.Loader.RefreshPlaytimeAsync(game, session);
        }
        catch (Exception e) when (e is SteamApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AppLog.Info($"{game.AppId}: playtime after the session: {e.Message}");
            return;
        }
        _main.Recompute(rebuildRows: false);
    }

    /// <summary>A status read nobody asked for: while the game runs, and when Steam writes its stats file.</summary>
    private async Task ReadAsync()
    {
        try
        {
            await _main.RefreshStateAsync();
        }
        catch (Exception e)
        {
            OnRepeatedError("Status poll", e);
        }
    }

    private void OnRepeatedError(string context, Exception e)
    {
        if (_repeatedError != e.Message) AppLog.Error(context, e);
        _repeatedError = e.Message;
        _main.StatusMessage = Loc.T("status.error", e.Message);
    }

    public void Dispose()
    {
        _timer.Stop();
        _statsFile?.Dispose();
    }
}
