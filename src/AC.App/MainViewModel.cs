using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>
/// The open game: its load, status, marks and edits, the list with its filters and the header's figures, recalculated
/// on every change. The other parts of the window are its properties.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _svc;
    private GameData? _game;
    /// <summary>The games whose status is being read: another game's read does not wait for theirs.</summary>
    private readonly HashSet<uint> _reading = new();
    private int _loadSeq;
    private int _busyCount;
    /// <summary>Hidden achievements whose description the user revealed ("appid/apiName"). Kept until the app closes.</summary>
    private readonly HashSet<string> _revealed = new(StringComparer.Ordinal);

    public MainViewModel(AppServices svc)
    {
        _svc = svc;
        Library = new LibraryViewModel(svc);
        Watch = new GameWatch(this, svc);
        Time = new TimePanelViewModel(this, svc);
        Overlay = new OverlayLists(this, svc);
        Session = new SessionViewModel(this, svc);
        Update = new UpdateNoticeViewModel(this, svc);
        Guides = new GuidesViewModel(this, svc);
        Files = new GameFilesViewModel(this, svc);
        Library.Picked += game => _ = SafeAsync(() => LoadAsync(game.AppId, game.Name));
        _rowsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Rows);

        _selectedSort = Options.Sorts[0];
        _kindFilter = Options.KindFilters[0];
        _selectedPercentile = Options.Percentiles.FirstOrDefault(p => p.Value == svc.Settings.Percentile) ?? Options.Percentiles[0];
        _selectedHours = Options.HourModes.FirstOrDefault(h => h.Value == svc.Settings.Hours) ?? Options.HourModes[0];

        RefreshLibraryCommand = new AsyncCommand(RefreshLibraryAsync, OnError);
        LoadAppIdCommand = new AsyncCommand(LoadAppIdAsync, OnError);
        ReloadCommand = new AsyncCommand(() => ReloadAsync(force: true), OnError, () => _game != null);
        RefreshStateCommand = new AsyncCommand(RefreshStateAsync, OnError, () => _game != null);
        ReadViaBridgeCommand = new AsyncCommand(ReadViaBridgeAsync, OnError, () => _game != null);
        FlushQueueCommand = new AsyncCommand(FlushQueueAsync, OnError, () => ManualUnlocks && QueueCount > 0);
        RevertCommand = new AsyncCommand(p => RevertAsync(p as AchievementRowViewModel), OnError, p => ManualUnlocks && p is AchievementRowViewModel { IsUnlocked: true });
        CompleteSelectedCommand = new AsyncCommand(CompleteSelectedAsync, OnError, p => _game != null && ManualUnlocks && RowsFrom(p).Any(r => !r.IsUnlocked));

        Library.Fill(svc.Library.Cached(), null);
    }

    public AppServices Services => _svc;
    public GameData? Game => _game;
    public event Action<string>? Toast;

    public GameWatch Watch { get; }
    public TimePanelViewModel Time { get; }
    public OverlayLists Overlay { get; }
    public SessionViewModel Session { get; }
    public UpdateNoticeViewModel Update { get; }
    public GuidesViewModel Guides { get; }
    public GameFilesViewModel Files { get; }

    // ---------- library ----------

    public LibraryViewModel Library { get; }

    private string _appIdInput = "";
    public string AppIdInput { get => _appIdInput; set => Set(ref _appIdInput, value); }

    // ---------- filters ----------

    private StatusFilter _status;
    public StatusFilter Status { get => _status; set { if (Set(ref _status, value)) Recompute(rebuildRows: true); } }

    private int _countAll;
    public int CountAll { get => _countAll; private set => Set(ref _countAll, value); }
    private int _countOpen;
    public int CountOpen { get => _countOpen; private set => Set(ref _countOpen, value); }
    private int _countDone;
    public int CountDone { get => _countDone; private set => Set(ref _countDone, value); }
    private int _countNotCounted;
    public int CountNotCounted { get => _countNotCounted; private set => Set(ref _countNotCounted, value); }

    private bool _onlyHidden;
    public bool OnlyHidden { get => _onlyHidden; set { if (Set(ref _onlyHidden, value)) Recompute(rebuildRows: true); } }

    private string _search = "";
    public string Search { get => _search; set { if (Set(ref _search, value)) Recompute(rebuildRows: true); } }

    private Option<AchievementKind?> _kindFilter;
    public Option<AchievementKind?> KindFilter { get => _kindFilter; set { if (Set(ref _kindFilter, value)) Recompute(rebuildRows: true); } }

    private Option<SortMode> _selectedSort;
    public Option<SortMode> SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!Set(ref _selectedSort, value)) return;
            OnPropertyChanged(nameof(ShowsHistory));
            Recompute(rebuildRows: true);
        }
    }

    private bool _descending;
    public bool Descending { get => _descending; set { if (Set(ref _descending, value)) Recompute(rebuildRows: true); } }

    private Option<Percentile> _selectedPercentile;
    public Option<Percentile> SelectedPercentile
    {
        get => _selectedPercentile;
        set
        {
            if (!Set(ref _selectedPercentile, value)) return;
            _svc.Settings.Percentile = value.Value;
            _svc.SaveSettings();
            Recompute(rebuildRows: true);
        }
    }

    private Option<HoursMode> _selectedHours;
    public Option<HoursMode> SelectedHours
    {
        get => _selectedHours;
        set
        {
            if (!Set(ref _selectedHours, value)) return;
            _svc.Settings.Hours = value.Value;
            _svc.SaveSettings();
            Recompute(rebuildRows: true);
        }
    }

    public bool ShowTimeLeft => _selectedHours.Value == HoursMode.Left;

    private Plan? _plan;
    private IReadOnlyList<AchievementRow> _rows = [];
    /// <summary>Minutes played since the last unlock when the time left was last counted.</summary>
    private double _played;
    private IReadOnlyDictionary<string, Countdown> _countdown = new Dictionary<string, Countdown>();

    /// <summary>Time left to a locked achievement now; null - done or no estimate.</summary>
    internal Countdown? CountdownOf(string apiName) => _countdown.TryGetValue(apiName, out var c) ? c : null;

    /// <summary>The hours the plan groups show; null - none, or longer than the wide margin.</summary>
    private double? HoursOf(AchievementRow r) => ShowTimeLeft ? CountdownOf(r.ApiName)?.Minutes : r.EstimateMinutes;

    /// <summary>The hours the overlay goes by (<see cref="Countdown.OrderMinutes"/>).</summary>
    internal double? OrderHoursOf(AchievementRow r) => ShowTimeLeft ? CountdownOf(r.ApiName)?.OrderMinutes : r.EstimateMinutes;

    private double PlayedSinceUnlock(GameData game)
    {
        var now = DateTimeOffset.Now;
        return Progress.MinutesSinceProgress(game, now, Watch.Sessions.Played(game.AppId, now));
    }

    public bool HideSpoilers => _svc.Settings.HideSpoilers;
    public bool HasApiKey => _svc.Settings.HasApiKey;
    /// <summary>Manual marks and Steam writes are enabled in settings.</summary>
    public bool ManualUnlocks => _svc.Settings.ManualUnlocks;

    /// <summary>What "Done" does, by the write mode.</summary>
    public string DoneTip => Loc.T(_svc.Settings.WriteMode switch
    {
        WriteMode.MarkAndUnlock => "done.write",
        WriteMode.Queue => "done.queue",
        _ => "done.mark",
    });

    // ---------- current game data ----------

    public ObservableCollection<AchievementRowViewModel> Rows { get; } = new();
    private readonly ListCollectionView _rowsView;
    /// <summary>The plan's headings with the achievements under them, for their "up to N h".</summary>
    private List<(ListSection Heading, List<string> ApiNames)> _timed = [];

    private AchievementRowViewModel? _selectedRow;
    /// <summary>Selected achievement, for the details panel.</summary>
    public AchievementRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { if (Set(ref _selectedRow, value)) OnPropertyChanged(nameof(HasSelectedRow)); }
    }
    public bool HasSelectedRow => _selectedRow != null;

    public bool HasGame => _game != null;
    public string? HeaderImageUrl => _game is null ? null : SteamImages.Header(_game.AppId);
    public string? HeroImageUrl => _game is null ? null : SteamImages.Hero(_game.AppId);

    // A three-part progress bar: unlocked in Steam, done but not counted, remaining (Grid columns in star units).
    private GridLength _segUnlocked = new(0, GridUnitType.Star);
    public GridLength SegUnlocked { get => _segUnlocked; private set => Set(ref _segUnlocked, value); }
    private GridLength _segManual = new(0, GridUnitType.Star);
    public GridLength SegManual { get => _segManual; private set => Set(ref _segManual, value); }
    private GridLength _segRest = new(1, GridUnitType.Star);
    public GridLength SegRest { get => _segRest; private set => Set(ref _segRest, value); }

    private string _progressShort = "";
    /// <summary>For the overlay: "24 of 32 (75%), +2 not counted".</summary>
    public string ProgressShort { get => _progressShort; private set => Set(ref _progressShort, value); }

    private string _statUnlocked = "-";
    public string StatUnlocked { get => _statUnlocked; private set => Set(ref _statUnlocked, value); }

    private string _statUnlockedSub = "";
    public string StatUnlockedSub { get => _statUnlockedSub; private set => Set(ref _statUnlockedSub, value); }

    private string _statRemaining = "-";
    public string StatRemaining { get => _statRemaining; private set => Set(ref _statRemaining, value); }

    private string _statRemainingSub = "";
    public string StatRemainingSub { get => _statRemainingSub; private set => Set(ref _statRemainingSub, value); }

    private IReadOnlyList<UnlockDay> _historyDays = [];
    public IReadOnlyList<UnlockDay> HistoryDays
    {
        get => _historyDays;
        private set
        {
            if (!Set(ref _historyDays, value)) return;
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(ShowsHistory));
        }
    }

    public bool HasHistory => _historyDays.Count > 0;

    /// <summary>The chart of unlocks goes over the list sorted by date.</summary>
    public bool ShowsHistory => HasHistory && _selectedSort.Value == SortMode.UnlockTime;

    private string _statPlayed = "-";
    public string StatPlayed { get => _statPlayed; private set => Set(ref _statPlayed, value); }

    private string _statPlayedSub = "";
    public string StatPlayedSub { get => _statPlayedSub; private set => Set(ref _statPlayedSub, value); }

    private string _gameName = "";
    public string GameName { get => _gameName; private set => Set(ref _gameName, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    private string _remainingText = "";
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    private string _statusMessage = Loc.T("status.ready");
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private int _queueCount;
    public int QueueCount
    {
        get => _queueCount;
        private set
        {
            if (!Set(ref _queueCount, value)) return;
            OnPropertyChanged(nameof(QueueText));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string QueueText => QueueCount > 0 ? Loc.T("toolbar.queueSend", QueueCount) : Loc.T("toolbar.queueEmpty");
    public bool QueueVisible => ManualUnlocks && _svc.Settings.WriteMode == WriteMode.Queue;

    public bool IsPending(string apiName) => ManualUnlocks && _game?.PendingUnlocks.Contains(apiName) == true;

    // ---------- commands ----------

    public AsyncCommand RefreshLibraryCommand { get; }
    public AsyncCommand LoadAppIdCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand RefreshStateCommand { get; }
    public AsyncCommand ReadViaBridgeCommand { get; }
    public AsyncCommand FlushQueueCommand { get; }
    public AsyncCommand RevertCommand { get; }
    /// <summary>"Done" for the rows selected in the list. Parameter: the list of selected rows.</summary>
    public AsyncCommand CompleteSelectedCommand { get; }

    public async Task StartAsync()
    {
        var running = await Watch.StartAsync();

        if (!_svc.Settings.HasApiKey)
        {
            StatusMessage = Loc.T("status.noKey");
        }
        else if (Library.Items.Count == 0)
        {
            await SafeAsync(RefreshLibraryAsync);
        }

        var last = _svc.Settings.LastAppId;
        var start = running.Count == 0 || running.Contains(last) ? last : PlayWatch.Followed(running);
        if (start != 0) await SafeAsync(() => LoadAsync(start, Library.NameOf(start)));
        await SafeAsync(Update.CheckAsync);
    }

    private async Task RefreshLibraryAsync()
    {
        await Busy(Loc.T("status.loadingLibrary"), async () =>
        {
            var games = await _svc.Library.RefreshAsync();
            Library.Fill(games, _game?.AppId);
            StatusMessage = Loc.Plural("status.libraryLoaded", games.Count);
        });
    }

    private async Task LoadAppIdAsync()
    {
        if (!uint.TryParse(AppIdInput.Trim(), out var id) || id == 0)
        {
            StatusMessage = Loc.T("status.badAppId");
            return;
        }
        await LoadAsync(id, Library.NameOf(id));
    }

    public async Task LoadAsync(uint appId, string? name)
    {
        var seq = ++_loadSeq;
        LoadReport? loaded = null;
        // A game opened before shows at once from its file: Steam's answers fill it in when they come, and what is
        // done to it meanwhile stays.
        var shown = _svc.Games.Cached(appId);
        if (shown is not null) SetGame(shown);
        await Busy(Loc.T("status.loadingGame", name ?? appId.ToString()), async () =>
        {
            var report = await _svc.Loader.LoadAsync(appId, name, running: Watch.Sessions.IsRunning(appId), current: shown);
            if (seq != _loadSeq) return; // another game picked while loading
            loaded = report;
            if (shown is null) SetGame(report.Game);
            else ShowLoaded(report.Game, sameGame: true);
            StatusMessage = report.Warnings.Count > 0
                ? string.Join(" | ", report.Warnings)
                : Loc.Plural("status.gameLoaded", report.Game.Achievements.Count);
            AnnounceNew(report.NewlyUnlocked);
        });
        // With the "Steam client" source the load skipped the status, so read it now, unless nothing can have changed:
        // browsing the library would show the user in every game opened.
        if (seq == _loadSeq && _game is { } opened && opened.AppId == appId &&
            (_svc.Settings.StatusSource == StatusSource.SteamClient || !_svc.Settings.HasApiKey) && _svc.Bridge.Exists &&
            _svc.Status.MayHaveChanged(opened, Watch.Sessions.IsRunning(appId)))
            await SafeAsync(RefreshStateAsync);

        if (seq == _loadSeq && _game is { } g && g.AppId == appId && _svc.Settings.HasApiKey && g.Achievements.Count > 0)
            await Time.CollectSampleIfDueAsync(g, loaded?.PercentagesMissing == true);

        if (seq == _loadSeq && _game is { } game && game.AppId == appId) _ = SafeAsync(() => Guides.LoadAsync(game));
    }

    private async Task ReloadAsync(bool force)
    {
        if (_game is null) return;
        await Busy(Loc.T("status.reloading"), async () =>
        {
            var seq = ++_loadSeq;
            var report = await _svc.Loader.LoadAsync(_game.AppId, _game.Name, forceSchema: force, running: Watch.Sessions.IsRunning(_game.AppId),
                current: _game);
            if (seq != _loadSeq) return;
            ShowLoaded(report.Game, sameGame: true);
            StatusMessage = report.Warnings.Count > 0 ? string.Join(" | ", report.Warnings) : Loc.T("status.reloaded");
            AnnounceNew(report.NewlyUnlocked);
            // The tips go with the game object, and a new content language needs its own.
            _ = SafeAsync(() => Guides.LoadAsync(report.Game));
        });
    }

    internal async Task RefreshStateAsync()
    {
        if (_game is not { } game || !_reading.Add(game.AppId)) return;
        try
        {
            var fresh = await _svc.Status.ReadAsync(game);
            Watch.StatusRead();
            if (!ReferenceEquals(game, _game)) return;
            // Do not rebuild the table while the user types in a row, otherwise the input is lost.
            bool editing = Keyboard.FocusedElement is FrameworkElement { DataContext: AchievementRowViewModel };
            Recompute(rebuildRows: fresh.Count > 0 && Status != StatusFilter.All && !editing);
            AnnounceNew(fresh);
            if (fresh.Count == 0) StatusMessage = Loc.T("status.refreshed", DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
        }
        finally
        {
            _reading.Remove(game.AppId);
        }
    }

    private void AnnounceNew(IReadOnlyList<string> apiNames)
    {
        if (_game is not { } game || apiNames.Count == 0) return;
        var text = Loc.T("status.newlyUnlocked", string.Join(", ", apiNames.Select(game.NameOf)));
        // Told at once, the player can still load a save before the game overwrites it.
        if (GameLogic.LeftBehind(game, apiNames) is { Count: > 0 } missed)
            text += ". " + Loc.T("status.leftBehind", string.Join(", ", missed.Select(game.NameOf)));
        StatusMessage = text;
        Toast?.Invoke(text);
    }

    private async Task ReadViaBridgeAsync()
    {
        if (_game is null) return;
        await Busy(Loc.T("status.readingClient"), async () =>
        {
            var (fresh, error) = await _svc.Status.ReadViaClientAsync(_game);
            if (error != null)
            {
                StatusMessage = Loc.T("error.client", error);
                return;
            }
            Recompute(rebuildRows: true);
            AnnounceNew(fresh);
            StatusMessage = Loc.T("status.readClient");
        });
    }

    private async Task FlushQueueAsync()
    {
        if (_game is null) return;
        if (MessageBox.Show(Loc.Plural("dialog.flushQueue", QueueCount), "Hundo",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await Busy(Loc.T("status.writing"), async () =>
        {
            var r = await _svc.Marks.FlushQueueAsync(_game);
            Recompute(rebuildRows: true);
            StatusMessage = r.Error is null ? Loc.T("status.queueSent") : Loc.T("status.writePartial", r.Error);
        });
    }

    private static IEnumerable<AchievementRowViewModel> RowsFrom(object? p) => p switch
    {
        AchievementRowViewModel r => new[] { r },
        System.Collections.IEnumerable list => list.OfType<AchievementRowViewModel>(),
        _ => Array.Empty<AchievementRowViewModel>(),
    };

    /// <summary>"Done" for the rows selected in the list; writing several to Steam at once is asked first.</summary>
    private async Task CompleteSelectedAsync(object? p)
    {
        var names = RowsFrom(p).Where(r => !r.IsUnlocked).Select(r => r.ApiName).ToList();
        if (names.Count > 1 && _svc.Settings.WriteMode == WriteMode.MarkAndUnlock &&
            MessageBox.Show(Loc.Plural("dialog.writeSelected", names.Count), "Hundo",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await CompleteAsync(names);
    }

    /// <summary>
    /// "Done" for achievements of the open game, wherever it is pressed and by time: marked, and in Steam what the write
    /// mode says (<see cref="MarkService.CompleteAsync"/>).
    /// </summary>
    /// <param name="inGame">From the overlay or by time: the player is in the game, so the result shows there too.</param>
    /// <param name="byTime">An auto mark.</param>
    internal async Task CompleteAsync(IReadOnlyList<string> apiNames, bool inGame = false, bool byTime = false)
    {
        if (_game is not { } game || apiNames.Count == 0) return;
        if (_svc.Settings.WriteMode == WriteMode.MarkAndUnlock) StatusMessage = Loc.T("status.writing");
        MarkReport r;
        try
        {
            r = await _svc.Marks.CompleteAsync(game, apiNames);
        }
        finally
        {
            if (ReferenceEquals(game, _game)) Recompute(rebuildRows: false);
        }
        if (!ReferenceEquals(game, _game)) return;
        var text = Loc.T((r.SteamWritten, byTime) switch
        {
            (true, true) => "status.autoWritten",
            (true, false) => "status.written",
            (false, true) => "status.autoMarked",
            _ => "status.marked",
        }, string.Join(", ", apiNames.Select(game.NameOf)));
        if (r.Error is { } error) text += " | " + (r.SteamWritten ? error : Loc.T("status.notWritten", error));
        StatusMessage = text;
        if (inGame) Toast?.Invoke(text);
    }

    private async Task RevertAsync(AchievementRowViewModel? row)
    {
        if (_game is null || row is null) return;
        if (!_svc.Marks.WasWrittenByApp(_game.AppId, row.ApiName) &&
            MessageBox.Show(Loc.T("dialog.revertForeign"),
                "Hundo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Busy(Loc.T("status.reverting"), async () =>
        {
            var r = await _svc.Marks.RevertAsync(_game, row.ApiName);
            Recompute(rebuildRows: false);
            StatusMessage = r.Error is null ? Loc.T("status.reverted", row.Name) : Loc.T("status.error", r.Error);
        });
    }

    // ---------- row edits ----------

    /// <summary>The "Done" box of a row: ticked, <see cref="CompleteAsync"/>; cleared, the mark is taken back.</summary>
    internal async void OnManualDoneChanged(AchievementRowViewModel row, bool done) => await SafeAsync(async () =>
    {
        if (done)
        {
            await CompleteAsync([row.ApiName]);
            return;
        }
        if (_game is not { } game) return;
        try
        {
            _svc.Marks.Uncomplete(game, row.ApiName);
        }
        finally
        {
            Recompute(rebuildRows: false);
        }
        StatusMessage = Loc.T("status.unmarked", row.Name);
    });

    internal void OnKindChanged(AchievementRowViewModel row, AchievementKind kind)
    {
        if (_game is null) return;
        _svc.Games.SetKind(_game, row.ApiName, kind);
        Recompute(rebuildRows: false);
    }

    internal void OnGroupChanged(AchievementRowViewModel row, string group)
    {
        if (_game is null) return;
        _svc.Games.SetGroup(_game, row.ApiName, group);
        Recompute(rebuildRows: false);
    }

    internal void OnNoteChanged(AchievementRowViewModel row, string note)
    {
        if (_game is null) return;
        _svc.Games.SetNote(_game, row.ApiName, note);
    }

    internal void OnInOverlayChanged(AchievementRowViewModel row, bool inOverlay)
    {
        if (_game is null) return;
        _svc.Games.SetOffOverlay(_game, row.ApiName, !inOverlay);
        Recompute(rebuildRows: false);
    }

    internal void OnEstimateChanged(AchievementRowViewModel row, double? minutes)
    {
        if (_game is null) return;
        _svc.Games.SetManualEstimate(_game, row.ApiName, minutes);
        Recompute(rebuildRows: false);
    }

    // ---------- recompute ----------

    private void SetGame(GameData game)
    {
        _game = game;
        // Here and not at the end of a load: a load dropped for a game picked meanwhile would take its place.
        if (_svc.Settings.LastAppId != game.AppId)
        {
            _svc.Settings.LastAppId = game.AppId;
            _svc.SaveSettings();
        }
        Guides.Clear();
        SelectedRow = null;
        OnPropertyChanged(nameof(HasGame));
        OnPropertyChanged(nameof(HeaderImageUrl));
        OnPropertyChanged(nameof(HeroImageUrl));
        Watch.WatchStatsFile(game);
        ShowLoaded(game);
        Library.ShowOpen(game.AppId);
        OnPropertyChanged(nameof(Game));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>What a load brought into the open game: its name, times and rows; the selected one stays.</summary>
    /// <param name="sameGame">The game was shown before the load: the user may be typing in it.</param>
    private void ShowLoaded(GameData game, bool sameGame = false)
    {
        GameName = game.Name.Length > 0 ? game.Name : $"AppID {game.AppId}";
        Time.Fill(game, sameGame);
        Recompute(rebuildRows: true);
    }

    public void SettingsChanged()
    {
        if (_game != null) _svc.Games.Relabel(_svc.Games.WithMarkSettings(_game));
        if (!ManualUnlocks && _status == StatusFilter.NotCounted)
        {
            _status = StatusFilter.All;
            OnPropertyChanged(nameof(Status));
        }
        OnPropertyChanged(nameof(HideSpoilers));
        OnPropertyChanged(nameof(ManualUnlocks));
        OnPropertyChanged(nameof(DoneTip));
        OnPropertyChanged(nameof(QueueVisible));
        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(QueueText));
        Update.RefreshText();
        Library.RefreshTexts();
        Watch.WatchStatsFile(_game);
        Recompute(rebuildRows: true);
        CommandManager.InvalidateRequerySuggested();
        if (_game != null && _svc.Loader.NeedsSchemaReload(_game)) _ = SafeAsync(() => ReloadAsync(force: false));
    }

    public void Recompute(bool rebuildRows)
    {
        if (_game is null) return;
        // What is typed in an achievement's box goes to it before its card is replaced, or it would be lost.
        if (rebuildRows)
            (Keyboard.FocusedElement as System.Windows.Controls.TextBox)?.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        var p = _selectedPercentile.Value;
        // The time to 100% derived from own progress changes with every unlock, so recalculate it.
        TimeEstimator.EstimateFromGlobalPercent(_game);
        var plan = Planner.Build(_game, p);
        _plan = plan;
        _played = PlayedSinceUnlock(_game);
        _countdown = plan.CountDown(_played);

        if (rebuildRows)
        {
            var query = new AchievementQuery
            {
                Status = Status,
                OnlyHidden = OnlyHidden,
                Search = Search,
                Sort = _selectedSort.Value,
                Descending = Descending,
                TimeLeft = ShowTimeLeft
                    ? _countdown.Where(kv => kv.Value.OrderMinutes.HasValue).ToDictionary(kv => kv.Key, kv => kv.Value.OrderMinutes!.Value)
                    : null,
            };
            if (_kindFilter.Value is { } k) query.Kinds.Add(k);
            var selectedApi = SelectedRow?.ApiName;
            Rows.Clear();
            GroupRows();
            foreach (var vm in WithHeadings(query.Apply(_game, p))) Rows.Add(vm);
            SelectedRow = selectedApi is null ? null : Rows.FirstOrDefault(r => r.ApiName == selectedApi);
        }

        var all = GameLogic.BuildRows(_game, p);
        _rows = all;
        if (!rebuildRows)
        {
            var fresh = all.ToDictionary(r => r.ApiName, StringComparer.Ordinal);
            foreach (var vm in Rows)
                if (fresh.TryGetValue(vm.ApiName, out var r)) vm.Update(r);
        }
        CountAll = all.Count;
        CountDone = all.Count(r => r.IsDone);
        CountOpen = CountAll - CountDone;
        CountNotCounted = all.Count(r => r.Status == EffectiveStatus.ManualDone);

        ShowHeadingNotes();

        var (u, m, total) = GameLogic.Progress(_game);
        var steamPct = Number(total == 0 ? 0 : 100.0 * u / total);
        var withManual = Number(total == 0 ? 0 : 100.0 * (u + m) / total);
        ProgressText = Loc.T("game.progress", u, total, steamPct) + (m > 0 ? Loc.T("game.progressManual", m, withManual) : "");
        ProgressShort = Loc.T("overlay.progress", u, total, steamPct) + (m > 0 ? Loc.T("overlay.progressManual", m) : "");
        SegUnlocked = new GridLength(u, GridUnitType.Star);
        SegManual = new GridLength(m, GridUnitType.Star);
        SegRest = new GridLength(total == 0 ? 1 : Math.Max(0, total - u - m), GridUnitType.Star);
        StatUnlocked = $"{u} / {total}";
        StatUnlockedSub = m > 0 ? Loc.T("game.unlockedSubManual", steamPct, m) : Loc.T("game.unlockedSub", steamPct);

        ShowRemaining();
        ShowPlayed();

        Time.ShowSample(_game);
        QueueCount = _game.PendingUnlocks.Count;
        HistoryDays = UnlockHistory.ByDay(_game, TimeZoneInfo.Local);
        Overlay.Update(all);

        Library.UpdateSummary(_game.AppId, GameSummary.Of(_game, _played));
        Library.UpdatePlaytime(_game.AppId, _game.PlaytimeMinutes);
        Session.Update();
    }

    /// <summary>The list sorted by plan or by date goes under headings; the other sorts are flat.</summary>
    private void GroupRows()
    {
        string[] levels = _selectedSort.Value switch
        {
            SortMode.Plan => [nameof(AchievementRowViewModel.Section), nameof(AchievementRowViewModel.Subsection)],
            SortMode.UnlockTime => [nameof(AchievementRowViewModel.Section)],
            _ => [],
        };
        var groups = _rowsView.GroupDescriptions;
        if (groups.OfType<PropertyGroupDescription>().Select(g => g.PropertyName).SequenceEqual(levels)) return;
        groups.Clear();
        foreach (var level in levels) groups.Add(new PropertyGroupDescription(level));
    }

    /// <summary>The rows under their headings: by plan the stages and the named groups in them, by date the days.</summary>
    private List<AchievementRowViewModel> WithHeadings(IReadOnlyList<AchievementRow> rows)
    {
        var headings = new Dictionary<string, ListSection>(StringComparer.OrdinalIgnoreCase);
        ListSection Heading(string key, string title) => headings.TryGetValue(key, out var h) ? h : headings[key] = new ListSection(title);
        var timed = new Dictionary<ListSection, List<string>>();
        void Time(ListSection heading, string apiName)
        {
            if (!timed.TryGetValue(heading, out var names)) timed[heading] = names = [];
            names.Add(apiName);
        }

        var language = CultureInfo.GetCultureInfo(Loc.Language);
        var days = new HashSet<ListSection>();
        var result = new List<AchievementRowViewModel>(rows.Count);
        foreach (var r in rows)
        {
            ListSection? section = null, subsection = null;
            switch (_selectedSort.Value)
            {
                case SortMode.Plan when r.IsDone:
                    section = Heading("done", Loc.T("list.done"));
                    subsection = ListSection.None;
                    break;
                case SortMode.Plan:
                    var stage = Planner.StageOf(r);
                    section = Heading("stage " + stage, Options.StageTitle(stage));
                    Time(section, r.ApiName);
                    // As the planner groups them: by the trimmed name, ignoring case.
                    var group = r.Group.Trim();
                    subsection = group.Length == 0 ? ListSection.None : Heading("group " + stage + " " + group, group);
                    if (subsection != ListSection.None) Time(subsection, r.ApiName);
                    break;
                case SortMode.UnlockTime when r.UnlockTime is { } time:
                    var day = DateOnly.FromDateTime(time.ToLocalTime().DateTime);
                    section = Heading("day " + day.DayNumber, day.ToString(Loc.T("history.date"), language));
                    days.Add(section);
                    break;
                case SortMode.UnlockTime:
                    section = r.IsDone ? Heading("done", Loc.T("list.done")) : Heading("open", Loc.T("list.open"));
                    break;
            }
            result.Add(new AchievementRowViewModel(this, r) { Section = section, Subsection = subsection });
        }
        // A number after a date reads as part of it: a day says how many it gave on the right.
        foreach (var under in result.Where(vm => vm.Section is not null).GroupBy(vm => vm.Section!))
        {
            if (days.Contains(under.Key)) under.Key.Note = "+" + under.Count().ToString(CultureInfo.CurrentCulture);
            else under.Key.Count = under.Count().ToString(CultureInfo.CurrentCulture);
        }
        _timed = timed.Select(kv => (kv.Key, kv.Value)).ToList();
        return result;
    }

    /// <summary>"Up to N h" on the plan's headings: the hours of the last achievement under each, as the cards show them.</summary>
    private void ShowHeadingNotes()
    {
        if (_timed.Count == 0) return;
        var rows = _rows.ToDictionary(r => r.ApiName, StringComparer.Ordinal);
        foreach (var (heading, names) in _timed)
        {
            var longest = names.Select(n => rows.TryGetValue(n, out var r) ? HoursOf(r) : null).Max();
            heading.Note = longest is null ? "" : Loc.T("plan.upTo", TimeEstimator.Format(longest));
        }
    }

    private void ShowRemaining()
    {
        if (_game is null || _plan is not { } plan) return;
        var left = plan.RemainingAfter(_played);
        RemainingText = plan.IsComplete ? Loc.T("plan.complete")
            : plan.Total == 0 ? Loc.T("plan.noAchievements")
            : left is { Minutes: { } rem }
                ? Loc.T("plan.remaining", TimeEstimator.Format(rem)) +
                  (plan.Pace > 1.05 ? Loc.T("plan.pace", Number(plan.Pace, "0.#")) : "") +
                  (plan.UnknownEstimateCount > 0 ? Loc.Plural("plan.unknown", plan.UnknownEstimateCount) : "")
            : left is not null ? Loc.T("plan.overdue")
            : Loc.T(_game.Achievements.All(a => a.Percent is null) ? "plan.noPercents" : "plan.noEstimate");

        StatRemaining = plan.IsComplete ? "100%"
            : left is { Minutes: { } rm } ? "~" + TimeEstimator.Format(rm)
            : Loc.T(left is not null ? "estimate.overdue" : "game.noEstimate");
        StatRemainingSub = plan.IsComplete ? Loc.T("game.allDone")
            : plan.Total == 0 ? Loc.T("game.noAchievements")
            : left is null ? Loc.T("game.setInTime")
            : CompletionSourceTitle(_game);
    }

    /// <summary>Counts the time left down with the play since the last unlock: once per minute of it.</summary>
    internal void CountDown()
    {
        if (_game is null || _plan is null) return;
        var played = PlayedSinceUnlock(_game);
        if (Math.Floor(played) == Math.Floor(_played)) return;
        _played = played;
        _countdown = _plan.CountDown(played);
        foreach (var vm in Rows) vm.RefreshTime();
        ShowHeadingNotes();
        Overlay.Update(_rows);
        ShowRemaining();
        ShowPlayed();
    }

    /// <summary>The played tile and the time summary: Steam's playtime goes on with the running session the app sees.</summary>
    private void ShowPlayed()
    {
        if (_game is null) return;
        var now = DateTimeOffset.Now;
        var played = TimeEstimator.Format(Progress.PlaytimeNow(_game, now, Watch.Sessions.Played(_game.AppId, now)));
        StatPlayed = played;
        StatPlayedSub = _game.PlaytimeOverrideMinutes is not null ? Loc.T("game.playedManual")
            : _game.PlaytimeOffsetMinutes > 0 ? Loc.T("game.playedOf", TimeEstimator.Format(_game.PlaytimeMinutes))
            : Watch.Sessions.IsRunning(_game.AppId) ? Loc.T("game.playedNow")
            : Loc.T("game.playedSteam");
        Time.ShowSummary(_game, played);
    }

    /// <summary>Time to 100% source, for the header.</summary>
    private static string CompletionSourceTitle(GameData g) =>
        g.CompletionMinutes is > 0
            ? g.CompletionSource == "manual" ? Loc.T("game.sourceManual") : Loc.T("game.sourceSite", g.CompletionSource)
            : Loc.T(g.SampleCompletionMinutes is > 0 ? "game.sourceSample"
                : g.DerivedCompletionMinutes is > 0 ? "game.sourceProgress"
                : "game.sourceEstimates");

    private static string Number(double value, string format = "0") => value.ToString(format, CultureInfo.CurrentCulture);

    // ---------- spoilers ----------

    public bool IsRevealed(string apiName) => _game is not null && _revealed.Contains(RevealKey(_game.AppId, apiName));

    /// <summary>Show the description of a hidden achievement everywhere it is on screen now.</summary>
    public void Reveal(string apiName)
    {
        if (_game is null || !_revealed.Add(RevealKey(_game.AppId, apiName))) return;
        foreach (var vm in Rows.Concat(Overlay.Nearest).Concat(Overlay.Missable))
            if (vm.ApiName == apiName) vm.RefreshSpoiler();
    }

    private static string RevealKey(uint appId, string apiName) => appId.ToString(CultureInfo.InvariantCulture) + "/" + apiName;

    // ---------- auto marks ----------

    /// <summary>
    /// A game seen running for less is not taken as played: Steam names a game while the app reads its status through
    /// the Steam client.
    /// </summary>
    private static readonly TimeSpan AutoMarkAfterRunning = TimeSpan.FromMinutes(1);

    /// <summary>With auto marks on, marks the achievement of the running open game whose time is up (<see cref="Plan.Due"/>).</summary>
    internal void MarkDue()
    {
        if (!ManualUnlocks || !_svc.Settings.AutoMark || IsBusy) return;
        if (_game is not { } game || _plan is not { } plan || _reading.Contains(game.AppId)) return;
        // Checked again here: the open game may have changed while the status was read.
        if (Watch.Sessions.RunningFor(game.AppId, DateTimeOffset.Now) is not { } running || running < AutoMarkAfterRunning) return;
        if (plan.Due(PlayedSinceUnlock(game)) is not { } name) return;
        // The plan is rebuilt once the mark is made, after a write to Steam: until then it still names this one.
        var state = game.StateOf(name);
        if (state.AutoMarkedAt is not null || GameLogic.StatusOf(state, game.CountManualMarks) != EffectiveStatus.Locked) return;
        state.AutoMarkedAt = DateTimeOffset.Now;
        _ = SafeAsync(() => CompleteAsync([name], inGame: true, byTime: true));
    }

    // ---------- helpers ----------

    internal async Task Busy(string message, Func<Task> action)
    {
        _busyCount++;
        IsBusy = true;
        StatusMessage = message;
        try
        {
            await action();
        }
        finally
        {
            if (--_busyCount == 0) IsBusy = false;
        }
    }

    internal async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { OnError(e); }
    }

    internal void OnError(Exception e)
    {
        AppLog.Error("Command", e);
        StatusMessage = Loc.T("status.error", e.Message);
    }

    internal void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            StatusMessage = Loc.T("status.browserFailed", e.Message);
        }
    }

    public void Dispose() => Watch.Dispose();
}
