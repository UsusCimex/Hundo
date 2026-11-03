using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>
/// The summary of the last session for the open game. Rebuilt with every status read, so unlocks read after the game
/// closed join it. Needs a way to read the status, otherwise it would claim nothing was unlocked.
/// </summary>
public sealed class SessionViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppServices _svc;

    public SessionViewModel(MainViewModel main, AppServices svc)
    {
        _main = main;
        _svc = svc;
        CloseCommand = new RelayCommand(() =>
        {
            if (main.Game is { } g) main.Watch.Dismiss(g.AppId);
            Update();
        });
    }

    private bool _isShown;
    public bool IsShown { get => _isShown; private set => Set(ref _isShown, value); }

    private string _title = "";
    public string Title { get => _title; private set => Set(ref _title, value); }

    private string _unlocked = "";
    public string Unlocked { get => _unlocked; private set => Set(ref _unlocked, value); }

    private string _left = "";
    public string Left { get => _left; private set => Set(ref _left, value); }

    public RelayCommand CloseCommand { get; }

    public void Update()
    {
        var now = DateTimeOffset.Now;
        var summary = _main.Game is { } game && _main.Watch.LastSession(game.AppId) is { } session && (_svc.Settings.HasApiKey || _svc.Bridge.Exists)
            ? SessionSummary.Of(game, session, _main.SelectedPercentile.Value, now, _main.Watch.Sessions.Played(game.AppId, now))
            : null;
        // A game started and closed at once is not worth a summary.
        if (summary is { Unlocked.Count: 0 } && summary.Duration < TimeSpan.FromMinutes(1)) summary = null;
        IsShown = summary is not null;
        if (summary is null) return;

        Title = Loc.T("session.title", TimeEstimator.Format(summary.Duration.TotalMinutes));
        Unlocked = summary.Unlocked.Count == 0 ? Loc.T("session.none")
            : Loc.Plural("session.unlocked", summary.Unlocked.Count, string.Join(", ", summary.Unlocked.Select(a => a.Name)));
        Left = summary.IsComplete ? Loc.T("plan.complete")
            : summary.LeftAfter is not { } after ? ""
            : summary.LeftBefore is { } before && Format(before) != Format(after) ? Loc.T("session.left", Format(before), Format(after))
            : Loc.T("session.leftNow", Format(after));

        static string Format(double minutes) => "~" + TimeEstimator.Format(minutes);
    }
}
