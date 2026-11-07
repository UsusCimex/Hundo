using System.Globalization;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>
/// The panel under the header: where the time to 100% comes from, the user's corrections of it and of the playtime,
/// and the player sample behind the estimates.
/// </summary>
public sealed class TimePanelViewModel : ObservableObject
{
    private static readonly TimeSpan SampleRefresh = TimeSpan.FromDays(7);

    private readonly MainViewModel _main;
    private readonly AppServices _svc;
    /// <summary>
    /// The hour boxes as the app last filled them or the user applied them: a box that differs holds a draft, which a
    /// load of the same game keeps. One that does not holds the kept value rounded, so applying it changes nothing.
    /// </summary>
    private (string Completion, string Override, string Offset) _filled = ("", "", "");
    private bool _sampleRefreshing;

    public TimePanelViewModel(MainViewModel main, AppServices svc)
    {
        _main = main;
        _svc = svc;
        FetchCompletionCommand = new AsyncCommand(FetchCompletionAsync, main.OnError, () => main.Game != null);
        ApplyCompletionCommand = new RelayCommand(ApplyCompletion, () => main.Game != null);
        ApplyPlaytimeCommand = new RelayCommand(ApplyPlaytime, () => main.Game != null);
        CollectSampleCommand = new AsyncCommand(() => CollectSampleAsync(auto: false), main.OnError, () => main.Game != null && svc.Settings.HasApiKey);
        CloseCommand = new RelayCommand(() => IsOpen = false);
    }

    private bool _isOpen;
    public bool IsOpen { get => _isOpen; set => Set(ref _isOpen, value); }

    private string _completionText = "";
    public string CompletionText { get => _completionText; private set => Set(ref _completionText, value); }

    private string _completionInput = "";
    /// <summary>Time to 100%, hours (manual input).</summary>
    public string CompletionInput { get => _completionInput; set => Set(ref _completionInput, value); }

    private string _playtimeOverrideInput = "";
    /// <summary>Own "actually played" hours. Empty - from Steam.</summary>
    public string PlaytimeOverrideInput { get => _playtimeOverrideInput; set => Set(ref _playtimeOverrideInput, value); }

    private string _playtimeOffsetInput = "";
    /// <summary>Steam hours to ignore (before achievements existed, AFK).</summary>
    public string PlaytimeOffsetInput { get => _playtimeOffsetInput; set => Set(ref _playtimeOffsetInput, value); }

    private string _sampleText = "";
    public string SampleText { get => _sampleText; private set => Set(ref _sampleText, value); }

    public AsyncCommand FetchCompletionCommand { get; }
    public RelayCommand ApplyCompletionCommand { get; }
    public RelayCommand ApplyPlaytimeCommand { get; }
    /// <summary>Collect own stats from review authors (percentages and time).</summary>
    public AsyncCommand CollectSampleCommand { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>The hour boxes of a loaded game.</summary>
    /// <param name="sameGame">The game was shown before the load: the user may be typing in it.</param>
    public void Fill(GameData game, bool sameGame)
    {
        var filled = (Completion: Hours(game.CompletionMinutes), Override: Hours(game.PlaytimeOverrideMinutes),
            Offset: game.PlaytimeOffsetMinutes > 0 ? Hours(game.PlaytimeOffsetMinutes) : "");
        if (!sameGame || CompletionInput == _filled.Completion) CompletionInput = filled.Completion;
        if (!sameGame || PlaytimeOverrideInput == _filled.Override) PlaytimeOverrideInput = filled.Override;
        if (!sameGame || PlaytimeOffsetInput == _filled.Offset) PlaytimeOffsetInput = filled.Offset;
        _filled = filled;
    }

    /// <summary>The time to 100% and where it comes from, with the playtime it goes by.</summary>
    /// <param name="played">The playtime now, formatted.</param>
    public void ShowSummary(GameData game, string played)
    {
        var playedNote = game.PlaytimeOverrideMinutes is not null ? Loc.T("time.playedNoteManual")
            : game.PlaytimeOffsetMinutes > 0 ? Loc.T("time.playedNoteOf", TimeEstimator.Format(game.PlaytimeMinutes))
            : "";
        CompletionText = game.CompletionMinutes is { } cm
            ? Loc.T("time.summaryKnown", TimeEstimator.Format(cm),
                game.CompletionSource == "manual" ? Loc.T("time.sourceManual") : game.CompletionSource, played, playedNote)
            : game.DerivedCompletionMinutes is { } dm
                ? Loc.T("time.summaryDerived", TimeEstimator.Format(dm), played, playedNote)
                : Loc.T("time.summaryPlayed", played, playedNote);
    }

    public void ShowSample(GameData game) =>
        SampleText = game.SampleFetchedAt is { } sf
            ? Loc.Plural("time.sample", game.SamplePlayers,
                game.SampleCompletionMinutes is { } sc
                    ? Loc.T("time.sampleCompleters", game.SampleCompleters, TimeEstimator.Format(sc))
                    : Loc.T("time.sampleNoCompleters"),
                sf.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)) +
              (game.Route is { } route ? " " + RouteSummary(route) : "")
            : Loc.T("time.sampleNone");

    private static string RouteSummary(RouteData route) =>
        Loc.T("time.route", route.Runs, TimeEstimator.Format(route.FirstRunMinutes));

    /// <summary>
    /// Neither Steam nor Steam Hunters has percentages (a new game): collects player statistics. A story game gets them
    /// in the background: the route through its story comes from how players went through it (<see cref="RouteModel"/>).
    /// An attempt, failed or not, is not repeated for a week; a collected sample is refreshed weekly in the background.
    /// </summary>
    public async Task CollectSampleIfDueAsync(GameData game, bool percentagesMissing)
    {
        bool tried = game.SampleTriedAt is { } at && DateTimeOffset.Now - at < SampleRefresh;
        if (percentagesMissing && game.SampleFetchedAt is null && !tried)
        {
            game.SampleTriedAt = DateTimeOffset.Now;
            _svc.Games.Save(game);
            await _main.SafeAsync(() => CollectSampleAsync(auto: true));
        }
        else if (game.SampleFetchedAt is null && !tried && RouteModel.HasFinale(game))
        {
            _ = RefreshSampleAsync(game, story: true);
        }
        else if (game.SampleFetchedAt is { } fetched && DateTimeOffset.Now - fetched > SampleRefresh && !tried)
        {
            _ = RefreshSampleAsync(game);
        }
    }

    /// <summary>
    /// Collects a sample without blocking the window: a week-old one again, or a story game's first. A much smaller
    /// fresh sample does not replace a good one; either way the next try is in a week. One at a time: a game opened
    /// meanwhile is tried when opened again.
    /// </summary>
    private async Task RefreshSampleAsync(GameData game, bool story = false)
    {
        if (_sampleRefreshing) return;
        _sampleRefreshing = true;
        try
        {
            game.SampleTriedAt = DateTimeOffset.Now;
            _svc.Games.Save(game);
            if (story) _main.StatusMessage = Loc.T("status.routeCollecting");
            var r = await _svc.Sample.CollectAsync(game);
            // The game may have been reopened meanwhile: apply to the current copy or the saved one, not a stale one.
            var target = _main.Game?.AppId == game.AppId ? _main.Game : _svc.Games.Cached(game.AppId);
            if (target is null) return;
            if (r.Players >= Math.Max(1, target.SamplePlayers / 2))
            {
                _svc.Sample.Apply(target, r);
            }
            else
            {
                target.SampleFetchedAt = DateTimeOffset.Now;
                _svc.Games.Save(target);
            }
            if (!ReferenceEquals(target, _main.Game)) return;
            _main.Recompute(rebuildRows: true);
            _main.StatusMessage = target.Route is { } route ? Loc.T("status.routeDone", RouteSummary(route))
                : Loc.Plural("status.sampleDone", r.Players);
        }
        catch (Exception e) when (e is SteamApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AppLog.Info($"{game.AppId}: sample refresh: {e.Message}");
            if (ReferenceEquals(game, _main.Game) && _main.StatusMessage == Loc.T("status.routeCollecting")) _main.StatusMessage = "";
        }
        finally
        {
            _sampleRefreshing = false;
        }
    }

    private async Task CollectSampleAsync(bool auto)
    {
        if (_main.Game is not { } game) return;
        var progress = new Progress<string>(m => _main.StatusMessage = m);
        await _main.Busy(Loc.T(auto ? "status.sampleAuto" : "status.sampleCollecting"), async () =>
        {
            var r = await _svc.Sample.CollectAsync(game, progress);
            var target = _main.Game?.AppId == game.AppId ? _main.Game! : game;
            _svc.Sample.Apply(target, r);
            if (!ReferenceEquals(target, _main.Game)) return;
            _main.Recompute(rebuildRows: true);
            _main.StatusMessage = Loc.Plural("status.sampleDone", r.Players) +
                                  (r.CompletionMedianMinutes is { } c ? Loc.T("status.sampleCompletion", r.Completers, TimeEstimator.Format(c)) : "") +
                                  (target.Route is { } route ? ". " + RouteSummary(route) : "");
        });
    }

    private async Task FetchCompletionAsync()
    {
        if (_main.Game is not { } game) return;
        await _main.Busy(Loc.T("status.fetchingCompletion"), async () =>
        {
            var m = await _svc.Loader.FetchCompletionAsync(game);
            if (m is null)
            {
                _main.StatusMessage = Loc.T("status.completionFailed");
                return;
            }
            CompletionInput = (m.Value / 60).ToString("0.#", CultureInfo.CurrentCulture);
            _filled.Completion = CompletionInput;
            _main.Recompute(rebuildRows: false);
            _main.StatusMessage = Loc.T("status.completionFetched");
        });
    }

    private void ApplyCompletion()
    {
        if (_main.Game is not { } game) return;
        if (CompletionInput == _filled.Completion)
        {
            _main.StatusMessage = Loc.T("status.completionSame");
            return;
        }
        var s = CompletionInput.Trim().Replace(',', '.');
        double? minutes = null;
        if (s.Length > 0)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || !TimeEstimator.IsValidMinutes(h * 60))
            {
                _main.StatusMessage = Loc.T("status.completionBad");
                return;
            }
            minutes = h * 60;
        }
        _svc.Games.SetCompletionMinutes(game, minutes, "manual");
        _filled.Completion = CompletionInput;
        _main.Recompute(rebuildRows: false);
        _main.StatusMessage = Loc.T(minutes is null ? "status.completionReset" : "status.estimatesUpdated");
    }

    private void ApplyPlaytime()
    {
        if (_main.Game is not { } game) return;
        if (!TryParseHours(PlaytimeOverrideInput, out var overrideMin) || !TryParseHours(PlaytimeOffsetInput, out var offsetMin))
        {
            _main.StatusMessage = Loc.T("status.playtimeBad");
            return;
        }
        if (PlaytimeOverrideInput == _filled.Override) overrideMin = game.PlaytimeOverrideMinutes;
        if (PlaytimeOffsetInput == _filled.Offset) offsetMin = game.PlaytimeOffsetMinutes;
        _svc.Games.SetPlaytime(game, overrideMin, offsetMin ?? 0);
        _filled.Override = PlaytimeOverrideInput;
        _filled.Offset = PlaytimeOffsetInput;
        _main.Recompute(rebuildRows: false);
        _main.StatusMessage = Loc.T(overrideMin is not null ? "status.playtimeManual"
            : offsetMin is > 0 ? "status.playtimeOffset" : "status.playtimeSteam");
    }

    /// <summary>Empty - null (success), hours >= 0 - minutes, else an error.</summary>
    private static bool TryParseHours(string text, out double? minutes)
    {
        minutes = null;
        var s = text.Trim().Replace(',', '.');
        if (s.Length == 0) return true;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || (h != 0 && !TimeEstimator.IsValidMinutes(h * 60)))
            return false;
        minutes = h * 60;
        return true;
    }

    private static string Hours(double? minutes) =>
        minutes is { } m ? (m / 60).ToString("0.#", CultureInfo.CurrentCulture) : "";
}
