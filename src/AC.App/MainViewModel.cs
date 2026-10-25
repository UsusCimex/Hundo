using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using AC.Core;
using AC.Infrastructure;
using Microsoft.Win32;

namespace AC.App;

public sealed record PlanGroupViewModel(string Label, string Longest, IReadOnlyList<AchievementRowViewModel> Items);

public sealed record PlanSectionViewModel(string Title, IReadOnlyList<PlanGroupViewModel> Groups);

public sealed record HistoryRowViewModel(string Date, string Count, string Names);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly GameService _svc;
    private readonly DispatcherTimer _runningTimer;
    private GameData? _game;
    private StatsFileWatcher? _watcher;
    private DateTime _lastPoll = DateTime.MinValue;
    private bool _refreshing;
    private uint _lastRunningAppId;
    private readonly SessionTracker _sessions;
    /// <summary>The last session seen from its start: its summary stays until dismissed or the next session starts.</summary>
    private PlaySession? _lastSession;
    private int _loadSeq;
    private int _busyCount;
    private string _completionFetchedText = "";
    private readonly Dictionary<uint, GameSummary> _summaries;
    /// <summary>Hidden achievements whose description the user revealed ("appid/apiName"). Kept until the app closes.</summary>
    private readonly HashSet<string> _revealed = new(StringComparer.Ordinal);
    private GuideTips? _guides;

    public MainViewModel(GameService svc)
    {
        _svc = svc;
        _summaries = new Dictionary<uint, GameSummary>(svc.Store.LoadSummaries());
        LibraryView = CollectionViewSource.GetDefaultView(Library);
        LibraryView.Filter = o => o is LibraryItemViewModel g &&
            (!LibraryStartedOnly || g.IsStarted) &&
            (string.IsNullOrWhiteSpace(_libraryFilter) || g.Name.Contains(_libraryFilter.Trim(), StringComparison.CurrentCultureIgnoreCase));

        _selectedSort = Options.Sorts[0];
        _kindFilter = Options.KindFilters[0];
        _selectedPercentile = Options.Percentiles.FirstOrDefault(p => p.Value == svc.Settings.Percentile) ?? Options.Percentiles[0];
        _selectedHours = Options.HourModes.FirstOrDefault(h => h.Value == svc.Settings.Hours) ?? Options.HourModes[0];
        _selectedLibrarySort = Options.LibrarySorts.FirstOrDefault(s => s.Value == svc.Settings.LibrarySort) ?? Options.LibrarySorts[0];
        ApplyLibrarySort();

        RefreshLibraryCommand = new AsyncCommand(RefreshLibraryAsync, OnError);
        LoadAppIdCommand = new AsyncCommand(LoadAppIdAsync, OnError);
        ReloadCommand = new AsyncCommand(() => ReloadAsync(force: true), OnError, () => _game != null);
        RefreshStateCommand = new AsyncCommand(RefreshStateAsync, OnError, () => _game != null);
        ReadViaBridgeCommand = new AsyncCommand(ReadViaBridgeAsync, OnError, () => _game != null);
        FlushQueueCommand = new AsyncCommand(FlushQueueAsync, OnError, () => ManualUnlocks && QueueCount > 0);
        FetchCompletionCommand = new AsyncCommand(FetchCompletionAsync, OnError, () => _game != null);
        ApplyCompletionCommand = new RelayCommand(ApplyCompletion, () => _game != null);
        ApplyPlaytimeCommand = new RelayCommand(ApplyPlaytime, () => _game != null);
        CollectSampleCommand = new AsyncCommand(() => CollectSampleAsync(auto: false), OnError, () => _game != null && _svc.HasApiKey);
        OpenGuideCommand = new RelayCommand(p => OpenGuide(p as string), _ => _game != null && SelectedRow != null);
        ExportCsvCommand = new RelayCommand(p => Export("csv", p as string == "manual"), _ => _game != null);
        ExportJsonCommand = new RelayCommand(p => Export("json", p as string == "manual"), _ => _game != null);
        ExportTagsCommand = new RelayCommand(ExportTags, () => _game != null);
        ImportTagsCommand = new RelayCommand(ImportTags, () => _game != null);
        RevertCommand = new AsyncCommand(p => RevertAsync(p as AchievementRowViewModel), OnError, p => ManualUnlocks && p is AchievementRowViewModel { IsUnlocked: true });
        UnlockInSteamCommand = new AsyncCommand(p => UnlockInSteamAsync(p, fromOverlay: false), OnError, p => _game != null && RowsFrom(p).Any(r => r.CanWriteToSteam));
        OverlayUnlockCommand = new AsyncCommand(p => UnlockInSteamAsync(p, fromOverlay: true), OnError, p => _game != null && RowsFrom(p).Any(r => r.CanWriteToSteam));
        OverlayMarkCommand = new RelayCommand(p => { if (p is AchievementRowViewModel r) MarkDoneFromOverlay(r); }, _ => ManualUnlocks);
        DownloadUpdateCommand = new RelayCommand(() => OpenUrl(_update?.DownloadUrl), () => _update != null);
        UpdateNotesCommand = new RelayCommand(() => OpenUrl(_update?.PageUrl), () => _update != null);
        CloseSessionCommand = new RelayCommand(() => { _lastSession = null; UpdateSession(); });
        OpenGuideTipCommand = new RelayCommand(p => OpenUrl(p is AchievementRowViewModel r ? GuideTipFor(r.ApiName)?.Url : null));
        TranslateTipCommand = new AsyncCommand(p => TranslateTipAsync(p as AchievementRowViewModel), OnError);

        uint runningAtStart = 0;
        try { runningAtStart = SteamLocal.RunningAppId(); }
        catch { }
        _sessions = new SessionTracker(runningAtStart, DateTimeOffset.Now);

        _runningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _runningTimer.Tick += async (_, _) => await OnRunningTickAsync();
        _runningTimer.Start();

        FillLibrary(svc.CachedLibrary());
    }

    public GameService Service => _svc;
    public GameData? Game => _game;
    public event Action<string>? Toast;

    // ---------- library ----------

    public ObservableCollection<LibraryItemViewModel> Library { get; } = new();
    public ICollectionView LibraryView { get; }

    private Option<LibrarySort> _selectedLibrarySort;
    public Option<LibrarySort> SelectedLibrarySort
    {
        get => _selectedLibrarySort;
        set
        {
            if (!Set(ref _selectedLibrarySort, value)) return;
            _svc.Settings.LibrarySort = value.Value;
            _svc.SaveSettings();
            ApplyLibrarySort();
        }
    }

    private bool LibraryByTimeLeft => _selectedLibrarySort.Value == LibrarySort.TimeLeft;

    public bool LibraryStartedOnly
    {
        get => _svc.Settings.LibraryStartedOnly;
        set
        {
            if (_svc.Settings.LibraryStartedOnly == value) return;
            _svc.Settings.LibraryStartedOnly = value;
            _svc.SaveSettings();
            OnPropertyChanged();
            LibraryView.Refresh();
        }
    }

    private void ApplyLibrarySort()
    {
        foreach (var item in Library) item.ShowLeft = LibraryByTimeLeft;
        if (LibraryView is ListCollectionView view) view.CustomSort = LibraryByTimeLeft ? LibraryOrder.ByTimeLeft : null;
    }

    private string _libraryFilter = "";
    public string LibraryFilter
    {
        get => _libraryFilter;
        set { if (Set(ref _libraryFilter, value)) LibraryView.Refresh(); }
    }

    private LibraryItemViewModel? _selectedLibraryGame;
    public LibraryItemViewModel? SelectedLibraryGame
    {
        get => _selectedLibraryGame;
        set
        {
            if (!Set(ref _selectedLibraryGame, value) || value is null) return;
            _ = SafeAsync(() => LoadAsync(value.AppId, value.Name));
        }
    }

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
    public Option<SortMode> SelectedSort { get => _selectedSort; set { if (Set(ref _selectedSort, value)) Recompute(rebuildRows: true); } }

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

    /// <summary>The hours the overlay and the plan groups go by; null - none, or longer than the wide margin.</summary>
    private double? HoursOf(AchievementRow r) => ShowTimeLeft ? CountdownOf(r.ApiName)?.Minutes : r.EstimateMinutes;

    private double PlayedSinceUnlock(GameData game)
    {
        var now = DateTimeOffset.Now;
        return Progress.MinutesSinceProgress(game, now, _sessions.Played(game.AppId, now));
    }

    public bool HideSpoilers => _svc.Settings.HideSpoilers;
    public bool HasApiKey => _svc.HasApiKey;
    /// <summary>Manual marks and Steam writes are enabled in settings.</summary>
    public bool ManualUnlocks => _svc.Settings.ManualUnlocks;

    public string PlanHint => Loc.T(ManualUnlocks ? "plan.hintManual" : "plan.hint");

    // ---------- current game data ----------

    public ObservableCollection<AchievementRowViewModel> Rows { get; } = new();
    public ObservableCollection<PlanSectionViewModel> PlanSections { get; } = new();
    /// <summary>Overlay: locked missables.</summary>
    public ObservableCollection<AchievementRowViewModel> OverlayMissable { get; } = new();
    /// <summary>Overlay: nearest by time.</summary>
    public ObservableCollection<AchievementRowViewModel> OverlayNearest { get; } = new();

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
        private set { if (Set(ref _historyDays, value)) OnPropertyChanged(nameof(HasHistory)); }
    }

    public bool HasHistory => _historyDays.Count > 0;

    /// <summary>The days of <see cref="HistoryDays"/>, newest first.</summary>
    private IReadOnlyList<HistoryRowViewModel> _historyRows = [];
    public IReadOnlyList<HistoryRowViewModel> HistoryRows { get => _historyRows; private set => Set(ref _historyRows, value); }

    private bool _hasSession;
    public bool HasSession { get => _hasSession; private set => Set(ref _hasSession, value); }

    private string _sessionTitle = "";
    public string SessionTitle { get => _sessionTitle; private set => Set(ref _sessionTitle, value); }

    private string _sessionUnlocked = "";
    public string SessionUnlocked { get => _sessionUnlocked; private set => Set(ref _sessionUnlocked, value); }

    private string _sessionLeft = "";
    public string SessionLeft { get => _sessionLeft; private set => Set(ref _sessionLeft, value); }

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

    private bool _autoFollow = true;
    /// <summary>Switch to the game running in Steam automatically.</summary>
    public bool AutoFollow { get => _autoFollow; set => Set(ref _autoFollow, value); }

    public bool IsPending(string apiName) => ManualUnlocks && _game?.PendingUnlocks.Contains(apiName) == true;

    // ---------- commands ----------

    public AsyncCommand RefreshLibraryCommand { get; }
    public AsyncCommand LoadAppIdCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand RefreshStateCommand { get; }
    public AsyncCommand ReadViaBridgeCommand { get; }
    public AsyncCommand FlushQueueCommand { get; }
    public AsyncCommand FetchCompletionCommand { get; }
    public RelayCommand ApplyCompletionCommand { get; }
    public RelayCommand ApplyPlaytimeCommand { get; }
    /// <summary>Collect own stats from review authors (percentages and time).</summary>
    public AsyncCommand CollectSampleCommand { get; }
    /// <summary>Look up how to get the selected achievement online: steam, discussions, youtube, google.</summary>
    public RelayCommand OpenGuideCommand { get; }
    public RelayCommand CloseSessionCommand { get; }
    /// <summary>Opens the guide section a tip comes from. Parameter: the row.</summary>
    public RelayCommand OpenGuideTipCommand { get; }
    /// <summary>Shows a tip in the interface language, or the original again. Parameter: the row.</summary>
    public AsyncCommand TranslateTipCommand { get; }

    private string _sampleText = "";
    public string SampleText { get => _sampleText; private set => Set(ref _sampleText, value); }

    public string? HeroImageUrl => _game is null ? null : SteamImages.Hero(_game.AppId);
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand ExportTagsCommand { get; }
    public RelayCommand ImportTagsCommand { get; }
    public AsyncCommand RevertCommand { get; }
    /// <summary>Write to Steam now. Parameter: a row or a list of selected rows.</summary>
    public AsyncCommand UnlockInSteamCommand { get; }
    /// <summary>Same from the overlay: no dialogs, they may hide behind the game.</summary>
    public AsyncCommand OverlayUnlockCommand { get; }
    public RelayCommand OverlayMarkCommand { get; }
    public RelayCommand DownloadUpdateCommand { get; }
    public RelayCommand UpdateNotesCommand { get; }

    // ---------- updates ----------

    private UpdateInfo? _update;
    public bool HasUpdate => _update != null;
    public string UpdateText => _update is { } u ? Loc.T("update.available", u.Version.ToString(3)) : "";

    private async Task CheckUpdateAsync()
    {
        if (!_svc.Settings.CheckUpdates) return;
        try
        {
            _update = await _svc.Updates.CheckAsync();
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppLog.Info("Update check: " + e.Message);
            return;
        }
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateText));
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task StartAsync()
    {
        if (!_svc.HasApiKey)
        {
            StatusMessage = Loc.T("status.noKey");
        }
        else if (Library.Count == 0)
        {
            await SafeAsync(RefreshLibraryAsync);
        }

        var running = SteamLocal.RunningAppId();
        var start = running != 0 ? running : _svc.Settings.LastAppId;
        if (start != 0) await SafeAsync(() => LoadAsync(start, Library.FirstOrDefault(g => g.AppId == start)?.Name));
        await SafeAsync(CheckUpdateAsync);
    }

    private async Task RefreshLibraryAsync()
    {
        await Busy(Loc.T("status.loadingLibrary"), async () =>
        {
            var games = await _svc.RefreshLibraryAsync();
            FillLibrary(games);
            StatusMessage = Loc.Plural("status.libraryLoaded", games.Count);
        });
    }

    private void FillLibrary(IEnumerable<OwnedGame> games)
    {
        Library.Clear();
        foreach (var g in games) Library.Add(new LibraryItemViewModel(g, _summaries.GetValueOrDefault(g.AppId)) { ShowLeft = LibraryByTimeLeft });
    }

    private async Task LoadAppIdAsync()
    {
        if (!uint.TryParse(AppIdInput.Trim(), out var id) || id == 0)
        {
            StatusMessage = Loc.T("status.badAppId");
            return;
        }
        await LoadAsync(id, Library.FirstOrDefault(g => g.AppId == id)?.Name);
    }

    public async Task LoadAsync(uint appId, string? name)
    {
        var seq = ++_loadSeq;
        LoadReport? loaded = null;
        await Busy(Loc.T("status.loadingGame", name ?? appId.ToString()), async () =>
        {
            var report = await _svc.LoadGameAsync(appId, name);
            if (seq != _loadSeq) return; // another game picked while loading
            loaded = report;
            SetGame(report.Game);
            StatusMessage = report.Warnings.Count > 0
                ? string.Join(" | ", report.Warnings)
                : Loc.Plural("status.gameLoaded", report.Game.Achievements.Count);
            AnnounceNew(report.NewlyUnlocked);
        });
        // With the "Steam client" source the load skipped the status, so read it now.
        if (seq == _loadSeq && _game?.AppId == appId &&
            (_svc.Settings.StatusSource == StatusSource.SteamClient || !_svc.HasApiKey) && _svc.Bridge.Exists)
            await SafeAsync(RefreshStateAsync);

        // Neither Steam nor Steam Hunters has percentages (a new game): collect player statistics ourselves.
        // An attempt, failed or not, is not repeated for a week; a collected sample is refreshed weekly in the background.
        if (seq == _loadSeq && _game is { } g && g.AppId == appId && _svc.HasApiKey && g.Achievements.Count > 0)
        {
            bool tried = g.SampleTriedAt is { } at && DateTimeOffset.Now - at < SampleRefresh;
            if (loaded?.PercentagesMissing == true && g.SampleFetchedAt is null && !tried)
            {
                g.SampleTriedAt = DateTimeOffset.Now;
                _svc.Store.SaveGame(g);
                await SafeAsync(() => CollectSampleAsync(auto: true));
            }
            else if (g.SampleFetchedAt is { } fetched && DateTimeOffset.Now - fetched > SampleRefresh && !tried)
            {
                g.SampleTriedAt = DateTimeOffset.Now;
                _svc.Store.SaveGame(g);
                _ = RefreshSampleAsync(g);
            }
        }

        if (seq == _loadSeq && _game is { } game && game.AppId == appId) _ = SafeAsync(() => LoadGuidesAsync(game));
    }

    public GuideTip? GuideTipFor(string apiName) => _guides?.Tips.GetValueOrDefault(apiName);

    public string? TipTranslation(string apiName) => _guides?.Translations.GetValueOrDefault(Loc.Language + ":" + apiName);

    /// <summary>Translates on demand; when the translator does not answer, the browser translates instead.</summary>
    private async Task TranslateTipAsync(AchievementRowViewModel? row)
    {
        if (row is null || _game is null || _guides is null) return;
        if (row.ShowsTranslation)
        {
            row.ShowTranslation(false);
            return;
        }
        try
        {
            if (await _svc.TranslateTipAsync(_game.AppId, _guides, row.ApiName, Loc.Language) is not null) row.ShowTranslation(true);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            AppLog.Info($"Translation: {e.Message}");
            StatusMessage = Loc.T("status.translateInBrowser");
            OpenUrl(Translator.WebUrl(GuideTipFor(row.ApiName)?.Text ?? "", Loc.Language));
        }
    }

    /// <summary>Tips from Steam guides, read in the background: the rows show them when they arrive.</summary>
    private async Task LoadGuidesAsync(GameData game)
    {
        var tips = await _svc.GetGuideTipsAsync(game);
        if (!ReferenceEquals(game, _game)) return;
        _guides = tips;
        foreach (var row in Rows) row.RefreshGuide();
    }

    private static readonly TimeSpan SampleRefresh = TimeSpan.FromDays(7);
    private bool _sampleRefreshing;

    /// <summary>
    /// Collects a week-old sample again without blocking the window. A much smaller fresh sample does not replace
    /// a good one; either way the next try is in a week.
    /// </summary>
    private async Task RefreshSampleAsync(GameData game)
    {
        if (_sampleRefreshing) return;
        _sampleRefreshing = true;
        try
        {
            var r = await _svc.CollectSampleAsync(game);
            // The game may have been reopened meanwhile: apply to the current copy or the saved one, not a stale one.
            var target = _game?.AppId == game.AppId ? _game : _svc.Cached(game.AppId);
            if (target is null) return;
            if (r.Players >= Math.Max(1, target.SamplePlayers / 2))
            {
                _svc.ApplySample(target, r);
            }
            else
            {
                target.SampleFetchedAt = DateTimeOffset.Now;
                _svc.Store.SaveGame(target);
            }
            if (!ReferenceEquals(target, _game)) return;
            Recompute(rebuildRows: true);
            StatusMessage = Loc.Plural("status.sampleDone", r.Players);
        }
        catch (Exception e) when (e is SteamApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AppLog.Info($"{game.AppId}: sample refresh: {e.Message}");
        }
        finally
        {
            _sampleRefreshing = false;
        }
    }

    private async Task CollectSampleAsync(bool auto)
    {
        if (_game is null) return;
        var game = _game;
        var progress = new Progress<string>(m => StatusMessage = m);
        await Busy(Loc.T(auto ? "status.sampleAuto" : "status.sampleCollecting"), async () =>
        {
            var r = await _svc.CollectSampleAsync(game, progress);
            var target = _game?.AppId == game.AppId ? _game! : game;
            _svc.ApplySample(target, r);
            if (!ReferenceEquals(target, _game)) return;
            Recompute(rebuildRows: true);
            StatusMessage = Loc.Plural("status.sampleDone", r.Players) +
                            (r.CompletionMedianMinutes is { } c ? Loc.T("status.sampleCompletion", r.Completers, TimeEstimator.Format(c)) : "");
        });
    }

    private void OpenGuide(string? kind)
    {
        if (_game is null || SelectedRow is null) return;
        var game = _game.Name.Length > 0 ? _game.Name : _game.AppId.ToString(CultureInfo.InvariantCulture);
        var ach = SelectedRow.Name;
        string q(string s) => Uri.EscapeDataString(s);
        var url = kind switch
        {
            "steam" => $"https://steamcommunity.com/app/{_game.AppId}/guides/?searchText={q(ach)}&browsefilter=trend&requiredtags%5B%5D=-1",
            "discussions" => $"https://steamcommunity.com/app/{_game.AppId}/discussions/search/?q={q(ach)}",
            "youtube" => $"https://www.youtube.com/results?search_query={q(game + " " + ach + " achievement")}",
            _ => $"https://www.google.com/search?q={q("\"" + game + "\" \"" + ach + "\" achievement guide")}",
        };
        OpenUrl(url);
    }

    private void OpenUrl(string? url)
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

    private async Task ReloadAsync(bool force)
    {
        if (_game is null) return;
        await Busy(Loc.T("status.reloading"), async () =>
        {
            var seq = ++_loadSeq;
            var report = await _svc.LoadGameAsync(_game.AppId, _game.Name, forceSchema: force);
            if (seq != _loadSeq) return;
            SetGame(report.Game);
            StatusMessage = report.Warnings.Count > 0 ? string.Join(" | ", report.Warnings) : Loc.T("status.reloaded");
            AnnounceNew(report.NewlyUnlocked);
        });
    }

    private async Task RefreshStateAsync()
    {
        if (_game is null || _refreshing) return;
        _refreshing = true;
        try
        {
            var game = _game;
            var fresh = await _svc.RefreshStateAutoAsync(game);
            _lastPoll = DateTime.Now;
            if (!ReferenceEquals(game, _game)) return;
            // Do not rebuild the table while the user types in a row, otherwise the input is lost.
            bool editing = Keyboard.FocusedElement is FrameworkElement { DataContext: AchievementRowViewModel };
            Recompute(rebuildRows: fresh.Count > 0 && Status != StatusFilter.All && !editing);
            AnnounceNew(fresh);
            if (fresh.Count == 0) StatusMessage = Loc.T("status.refreshed", DateTime.Now.ToString("T", CultureInfo.CurrentCulture));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ReadViaBridgeAsync()
    {
        if (_game is null) return;
        await Busy(Loc.T("status.readingClient"), async () =>
        {
            var (fresh, error) = await _svc.RefreshViaBridgeAsync(_game);
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
            var r = await _svc.FlushQueueAsync(_game);
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

    private async Task UnlockInSteamAsync(object? p, bool fromOverlay)
    {
        if (_game is null) return;
        var rows = RowsFrom(p).Where(r => r.CanWriteToSteam).ToList();
        if (rows.Count == 0) return;

        if (!fromOverlay && rows.Count > 1 &&
            MessageBox.Show(Loc.Plural("dialog.writeSelected", rows.Count), "Hundo",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var game = _game;
        await Busy(Loc.T("status.writing"), async () =>
        {
            var r = await _svc.UnlockInSteamAsync(game, rows.Select(x => x.ApiName).ToList());
            if (!ReferenceEquals(game, _game)) return;
            Recompute(rebuildRows: false);
            var names = string.Join(", ", rows.Select(x => x.Name));
            if (r.SteamWritten)
            {
                StatusMessage = Loc.T("status.written", names) + (r.Error is null ? "" : " | " + r.Error);
                Toast?.Invoke(Loc.T("status.writtenToast", names));
            }
            else StatusMessage = Loc.T("status.notWritten", r.Error ?? Loc.T("status.unknownError"));
        });
    }

    private async Task RevertAsync(AchievementRowViewModel? row)
    {
        if (_game is null || row is null) return;
        if (!_svc.WasWrittenByApp(_game.AppId, row.ApiName) &&
            MessageBox.Show(Loc.T("dialog.revertForeign"),
                "Hundo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Busy(Loc.T("status.reverting"), async () =>
        {
            var r = await _svc.RevertInSteamAsync(_game, row.ApiName);
            Recompute(rebuildRows: false);
            StatusMessage = r.Error is null ? Loc.T("status.reverted", row.Name) : Loc.T("status.error", r.Error);
        });
    }

    private async Task FetchCompletionAsync()
    {
        if (_game is null) return;
        await Busy(Loc.T("status.fetchingCompletion"), async () =>
        {
            var m = await _svc.FetchCompletionAsync(_game);
            if (m is null)
            {
                StatusMessage = Loc.T("status.completionFailed");
                return;
            }
            CompletionInput = (m.Value / 60).ToString("0.#", CultureInfo.CurrentCulture);
            _completionFetchedText = CompletionInput;
            Recompute(rebuildRows: false);
            StatusMessage = Loc.T("status.completionFetched");
        });
    }

    private void ApplyCompletion()
    {
        if (_game is null) return;
        if (_game.CompletionMinutes is not null && _completionFetchedText.Length > 0 && CompletionInput == _completionFetchedText)
        {
            StatusMessage = Loc.T("status.completionSame");
            return;
        }
        var s = CompletionInput.Trim().Replace(',', '.');
        double? minutes = null;
        if (s.Length > 0)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || h <= 0)
            {
                StatusMessage = Loc.T("status.completionBad");
                return;
            }
            minutes = h * 60;
        }
        _svc.SetCompletionMinutes(_game, minutes, "manual");
        Recompute(rebuildRows: false);
        StatusMessage = Loc.T(minutes is null ? "status.completionReset" : "status.estimatesUpdated");
    }

    private void ApplyPlaytime()
    {
        if (_game is null) return;
        if (!TryParseHours(PlaytimeOverrideInput, out var overrideMin) || !TryParseHours(PlaytimeOffsetInput, out var offsetMin))
        {
            StatusMessage = Loc.T("status.playtimeBad");
            return;
        }
        _svc.SetPlaytime(_game, overrideMin, offsetMin ?? 0);
        Recompute(rebuildRows: false);
        StatusMessage = Loc.T(overrideMin is not null ? "status.playtimeManual"
            : offsetMin is > 0 ? "status.playtimeOffset" : "status.playtimeSteam");
    }

    /// <summary>Empty - null (success), hours >= 0 - minutes, else an error.</summary>
    private static bool TryParseHours(string text, out double? minutes)
    {
        minutes = null;
        var s = text.Trim().Replace(',', '.');
        if (s.Length == 0) return true;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || h < 0) return false;
        minutes = h * 60;
        return true;
    }

    private static string Hours(double? minutes) =>
        minutes is { } m ? (m / 60).ToString("0.#", CultureInfo.CurrentCulture) : "";

    // ---------- row edits ----------

    internal async void OnManualDoneChanged(AchievementRowViewModel row, bool done)
    {
        if (_game is null) return;
        try
        {
            var report = await _svc.SetManualDoneAsync(_game, row.ApiName, done);
            if (report.Error != null) StatusMessage = report.Error;
            else if (report.SteamWritten) StatusMessage = Loc.T("status.written", row.Name);
            else StatusMessage = Loc.T(done ? "status.marked" : "status.unmarked", row.Name);
        }
        catch (Exception e)
        {
            OnError(e);
        }
        Recompute(rebuildRows: false);
    }

    internal void OnKindChanged(AchievementRowViewModel row, AchievementKind kind)
    {
        if (_game is null) return;
        _svc.SetKind(_game, row.ApiName, kind);
        Recompute(rebuildRows: false);
    }

    internal void OnGroupChanged(AchievementRowViewModel row, string group)
    {
        if (_game is null) return;
        _svc.SetGroup(_game, row.ApiName, group);
        Recompute(rebuildRows: false);
    }

    internal void OnNoteChanged(AchievementRowViewModel row, string note)
    {
        if (_game is null) return;
        _svc.SetNote(_game, row.ApiName, note);
    }

    internal void OnEstimateChanged(AchievementRowViewModel row, double? minutes)
    {
        if (_game is null) return;
        _svc.SetManualEstimate(_game, row.ApiName, minutes);
        Recompute(rebuildRows: false);
    }

    /// <summary>Mark from overlay.</summary>
    public void MarkDoneFromOverlay(AchievementRowViewModel row)
    {
        if (!ManualUnlocks || row.IsDone) return;
        row.ManualDone = true;
    }

    // ---------- export / import ----------

    private void Export(string format, bool manualOnly)
    {
        if (_game is null) return;
        var scope = manualOnly ? ExportScope.ManualOnly : ExportScope.All;
        var dlg = new SaveFileDialog
        {
            FileName = $"{Sanitize(_game.Name)}_{_game.AppId}{(manualOnly ? "_not_counted" : "")}.{format}",
            Filter = format == "csv" ? "CSV (*.csv)|*.csv" : "JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (format == "csv") File.WriteAllBytes(dlg.FileName, Exporter.ToCsv(_game, scope, _svc.Settings.CsvSeparator));
            else File.WriteAllText(dlg.FileName, Exporter.ToJson(_game, scope));
            StatusMessage = Loc.T("status.saved", dlg.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = Loc.T("status.saveFailedLocked", e.Message);
        }
    }

    private void ExportTags()
    {
        if (_game is null) return;
        var dlg = new SaveFileDialog { FileName = $"{Sanitize(_game.Name)}_{_game.AppId}_tags.json", Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, TagPack.From(_game).ToJson());
            StatusMessage = Loc.T("status.tagsSaved", dlg.FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = Loc.T("status.saveFailed", e.Message);
        }
    }

    private void ImportTags()
    {
        if (_game is null) return;
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var n = _svc.ImportTags(_game, File.ReadAllText(dlg.FileName));
            Recompute(rebuildRows: true);
            StatusMessage = Loc.Plural("status.tagsApplied", n);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = Loc.T("status.tagsBad", e.Message);
        }
    }

    // ---------- recompute ----------

    private void SetGame(GameData game)
    {
        _game = game;
        _guides = null;
        GameName = game.Name.Length > 0 ? game.Name : $"AppID {game.AppId}";
        SelectedRow = null;
        OnPropertyChanged(nameof(HasGame));
        OnPropertyChanged(nameof(HeaderImageUrl));
        OnPropertyChanged(nameof(HeroImageUrl));
        CompletionInput = Hours(game.CompletionMinutes);
        _completionFetchedText = game.CompletionSource == "manual" ? "" : CompletionInput;
        PlaytimeOverrideInput = Hours(game.PlaytimeOverrideMinutes);
        PlaytimeOffsetInput = game.PlaytimeOffsetMinutes > 0 ? Hours(game.PlaytimeOffsetMinutes) : "";
        RestartWatcher();
        Recompute(rebuildRows: true);
        OnPropertyChanged(nameof(Game));
        CommandManager.InvalidateRequerySuggested();
    }

    public void SettingsChanged()
    {
        if (_game != null)
        {
            _game.CountManualMarks = ManualUnlocks;
            _svc.Relabel(_game);
        }
        if (!ManualUnlocks && _status == StatusFilter.NotCounted)
        {
            _status = StatusFilter.All;
            OnPropertyChanged(nameof(Status));
        }
        OnPropertyChanged(nameof(HideSpoilers));
        OnPropertyChanged(nameof(ManualUnlocks));
        OnPropertyChanged(nameof(PlanHint));
        OnPropertyChanged(nameof(QueueVisible));
        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(UpdateText));
        foreach (var item in Library) item.RefreshTexts();
        RestartWatcher();
        Recompute(rebuildRows: true);
        CommandManager.InvalidateRequerySuggested();
        if (_game != null && _svc.NeedsSchemaReload(_game)) _ = SafeAsync(() => ReloadAsync(force: false));
    }

    public void Recompute(bool rebuildRows)
    {
        if (_game is null) return;
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
                    ? _countdown.Where(kv => kv.Value.Minutes.HasValue).ToDictionary(kv => kv.Key, kv => kv.Value.Minutes!.Value)
                    : null,
            };
            if (_kindFilter.Value is { } k) query.Kinds.Add(k);
            var selectedApi = SelectedRow?.ApiName;
            Rows.Clear();
            foreach (var r in query.Apply(_game, p)) Rows.Add(new AchievementRowViewModel(this, r));
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

        ShowPlan(plan);

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
        var played = TimeEstimator.Format(_game.EffectivePlaytimeMinutes);
        var steamPlayed = TimeEstimator.Format(_game.PlaytimeMinutes);
        StatPlayed = played;
        StatPlayedSub = _game.PlaytimeOverrideMinutes is not null ? Loc.T("game.playedManual")
            : _game.PlaytimeOffsetMinutes > 0 ? Loc.T("game.playedOf", steamPlayed)
            : Loc.T("game.playedSteam");
        var playedNote = _game.PlaytimeOverrideMinutes is not null ? Loc.T("time.playedNoteManual")
            : _game.PlaytimeOffsetMinutes > 0 ? Loc.T("time.playedNoteOf", steamPlayed)
            : "";
        CompletionText = _game.CompletionMinutes is { } cm
            ? Loc.T("time.summaryKnown", TimeEstimator.Format(cm),
                _game.CompletionSource == "manual" ? Loc.T("time.sourceManual") : _game.CompletionSource, played, playedNote)
            : _game.DerivedCompletionMinutes is { } dm
                ? Loc.T("time.summaryDerived", TimeEstimator.Format(dm), played, playedNote)
                : Loc.T("time.summaryPlayed", played, playedNote);

        SampleText = _game.SampleFetchedAt is { } sf
            ? Loc.Plural("time.sample", _game.SamplePlayers,
                _game.SampleCompletionMinutes is { } sc
                    ? Loc.T("time.sampleCompleters", _game.SampleCompleters, TimeEstimator.Format(sc))
                    : Loc.T("time.sampleNoCompleters"),
                sf.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
            : Loc.T("time.sampleNone");
        QueueCount = _game.PendingUnlocks.Count;
        var history = UnlockHistory.ByDay(_game, TimeZoneInfo.Local);
        var language = CultureInfo.GetCultureInfo(Loc.Language);
        HistoryDays = history;
        HistoryRows = history.Reverse().Select(d => new HistoryRowViewModel(
            d.Day.ToString(Loc.T("history.date"), language), $"+{d.Unlocked.Count}", string.Join(", ", d.Unlocked.Select(a => a.Name)))).ToList();
        UpdateOverlayLists(all);

        var summary = GameSummary.Of(_game, _played);
        _summaries[_game.AppId] = summary;
        if (Library.FirstOrDefault(i => i.AppId == _game.AppId) is { } item && !summary.Equals(item.Summary))
        {
            item.Summary = summary;
            if (LibraryByTimeLeft || LibraryStartedOnly) LibraryView.Refresh();
        }
        UpdateSession();
    }

    private void ShowPlan(Plan plan)
    {
        PlanSections.Clear();
        foreach (var s in plan.Sections)
        {
            var groups = s.Groups.Select(g =>
            {
                var longest = ShowTimeLeft ? g.Items.Max(HoursOf) : g.LongestMinutes;
                return new PlanGroupViewModel(
                    g.Label.Length > 0 ? g.Label : Loc.T("plan.noGroup"),
                    longest is null ? "" : Loc.T("plan.upTo", TimeEstimator.Format(longest)),
                    g.Items.Select(i => new AchievementRowViewModel(this, i)).ToList());
            }).ToList();
            PlanSections.Add(new PlanSectionViewModel($"{Options.StageTitle(s.Stage)} ({s.Count})", groups));
        }
    }

    private void ShowRemaining()
    {
        if (_game is null || _plan is not { } plan) return;
        var left = plan.RemainingAfter(_played);
        RemainingText = plan.IsComplete ? Loc.T("plan.complete")
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
            : left is null ? Loc.T("game.setInTime")
            : CompletionSourceTitle(_game);
    }

    /// <summary>Counts the time left down with the play since the last unlock: once per minute of it.</summary>
    private void CountDown()
    {
        if (_game is null || _plan is null) return;
        var played = PlayedSinceUnlock(_game);
        if (Math.Floor(played) == Math.Floor(_played)) return;
        _played = played;
        _countdown = _plan.CountDown(played);
        foreach (var vm in Rows) vm.RefreshTime();
        ShowPlan(_plan);
        UpdateOverlayLists(_rows);
        ShowRemaining();
    }

    /// <summary>Time to 100% source, for the header.</summary>
    private static string CompletionSourceTitle(GameData g) =>
        g.CompletionMinutes is > 0
            ? g.CompletionSource == "manual" ? Loc.T("game.sourceManual") : Loc.T("game.sourceSite", g.CompletionSource)
            : Loc.T(g.SampleCompletionMinutes is > 0 ? "game.sourceSample"
                : g.DerivedCompletionMinutes is > 0 ? "game.sourceProgress"
                : "game.sourceEstimates");

    private static string Number(double value, string format = "0") => value.ToString(format, CultureInfo.CurrentCulture);

    private void UpdateOverlayLists(IReadOnlyList<AchievementRow> all)
    {
        OverlayMissable.Clear();
        foreach (var r in all.Where(r => !r.IsDone && r.Kind == AchievementKind.Missable)
                             .OrderBy(r => HoursOf(r) ?? double.MaxValue).Take(6))
            OverlayMissable.Add(new AchievementRowViewModel(this, r));

        OverlayNearest.Clear();
        foreach (var r in all.Where(r => !r.IsDone && r.Kind != AchievementKind.Missable)
                             .OrderBy(r => HoursOf(r) is null)
                             .ThenBy(HoursOf)
                             .ThenByDescending(r => r.Def.Percent ?? -1)
                             .Take(Math.Max(1, _svc.Settings.OverlayNearestCount)))
            OverlayNearest.Add(new AchievementRowViewModel(this, r));
    }

    // ---------- spoilers ----------

    public bool IsRevealed(string apiName) => _game is not null && _revealed.Contains(RevealKey(_game.AppId, apiName));

    /// <summary>Show the description of a hidden achievement everywhere it is on screen now.</summary>
    public void Reveal(string apiName)
    {
        if (_game is null || !_revealed.Add(RevealKey(_game.AppId, apiName))) return;
        foreach (var vm in Rows.Concat(PlanSections.SelectMany(s => s.Groups).SelectMany(g => g.Items)))
            if (vm.ApiName == apiName) vm.RefreshSpoiler();
    }

    private static string RevealKey(uint appId, string apiName) => appId.ToString(CultureInfo.InvariantCulture) + "/" + apiName;

    // ---------- game tracking ----------

    private void RestartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (_game is null || _svc.Settings.SteamId64 == 0) return;
        var w = new StatsFileWatcher(_svc.Settings.SteamId64, _game.AppId);
        w.Changed += () => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Reading via the Steam client can rewrite the file itself - avoid a loop.
            if (DateTime.Now - _lastPoll < TimeSpan.FromSeconds(10)) return;
            if (_svc.HasApiKey || _svc.Bridge.Exists) _ = SafeAsync(RefreshStateAsync);
        });
        _watcher = w;
    }

    private async Task OnRunningTickAsync()
    {
        uint running;
        try { running = SteamLocal.RunningAppId(); }
        catch { return; }

        if (running != _lastRunningAppId)
        {
            // While something loads, do not remember it; retry on the next tick.
            if (IsBusy) return;
            if (_sessions.Observe(running, DateTimeOffset.Now) is { } ended) _lastSession = ended;
            else if (running != 0) _lastSession = null;
            UpdateSession();
            if (_lastRunningAppId != 0 && _game is { } played && played.AppId == _lastRunningAppId && _svc.HasApiKey)
            {
                var session = _lastSession;
                _ = SafeAsync(() => AfterSessionAsync(played, session));
            }
            _lastRunningAppId = running;
            if (running != 0 && AutoFollow && running != _game?.AppId)
            {
                await SafeAsync(() => LoadAsync(running, Library.FirstOrDefault(g => g.AppId == running)?.Name));
                return;
            }
        }

        // While the game runs, poll the status (in case the stats file did not change).
        bool canRefresh = _svc.HasApiKey || _svc.Bridge.Exists;
        if (running != 0 && running == _game?.AppId && canRefresh && !IsBusy &&
            DateTime.Now - _lastPoll > TimeSpan.FromSeconds(Math.Max(15, _svc.Settings.PollSeconds)))
        {
            _lastPoll = DateTime.Now;
            await SafeAsync(RefreshStateAsync);
        }
        CountDown();
    }

    /// <summary>Steam counts a session's playtime a little after it ends; the new point goes to the playtime history.</summary>
    /// <param name="session">The session if the app saw it from its start.</param>
    private async Task AfterSessionAsync(GameData game, PlaySession? session)
    {
        await Task.Delay(TimeSpan.FromMinutes(2));
        if (_game != game || IsBusy || SteamLocal.RunningAppId() == game.AppId) return;
        try
        {
            await _svc.RefreshPlaytimeAsync(game, session);
        }
        catch (Exception e) when (e is SteamApiException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            AppLog.Info($"{game.AppId}: playtime after the session: {e.Message}");
            return;
        }
        Recompute(rebuildRows: false);
    }

    /// <summary>
    /// The summary of the last session for the open game. Rebuilt with every status read, so unlocks read after
    /// the game closed join it. Needs a way to read the status, otherwise it would claim nothing was unlocked.
    /// </summary>
    private void UpdateSession()
    {
        var now = DateTimeOffset.Now;
        var summary = _lastSession is { } session && _game is { } game && game.AppId == session.AppId && (_svc.HasApiKey || _svc.Bridge.Exists)
            ? SessionSummary.Of(game, session, _selectedPercentile.Value, now, _sessions.Played(game.AppId, now))
            : null;
        // A game started and closed at once is not worth a summary.
        if (summary is { Unlocked.Count: 0 } && summary.Duration < TimeSpan.FromMinutes(1)) summary = null;
        HasSession = summary is not null;
        if (summary is null) return;

        SessionTitle = Loc.T("session.title", TimeEstimator.Format(summary.Duration.TotalMinutes));
        SessionUnlocked = summary.Unlocked.Count == 0 ? Loc.T("session.none")
            : Loc.Plural("session.unlocked", summary.Unlocked.Count, string.Join(", ", summary.Unlocked.Select(a => a.Name)));
        SessionLeft = summary.IsComplete ? Loc.T("plan.complete")
            : summary.LeftAfter is not { } after ? ""
            : summary.LeftBefore is { } before && Left(before) != Left(after) ? Loc.T("session.left", Left(before), Left(after))
            : Loc.T("session.leftNow", Left(after));

        static string Left(double minutes) => "~" + TimeEstimator.Format(minutes);
    }

    private void AnnounceNew(IReadOnlyList<string> apiNames)
    {
        if (_game is null || apiNames.Count == 0) return;
        var names = apiNames.Select(n => _game.Achievements.FirstOrDefault(a => a.ApiName == n)?.Name ?? n).ToList();
        var text = Loc.T("status.newlyUnlocked", string.Join(", ", names));
        StatusMessage = text;
        Toast?.Invoke(text);
    }

    // ---------- helpers ----------

    private async Task Busy(string message, Func<Task> action)
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

    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) { OnError(e); }
    }

    private void OnError(Exception e)
    {
        AppLog.Error("Command", e);
        StatusMessage = Loc.T("status.error", e.Message);
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length == 0 ? "game" : s;
    }

    public void Dispose()
    {
        _runningTimer.Stop();
        _watcher?.Dispose();
    }
}

/// <summary>Library orders other than Steam's own.</summary>
internal sealed class LibraryOrder : System.Collections.IComparer
{
    public static readonly LibraryOrder ByTimeLeft = new();

    public int Compare(object? x, object? y)
    {
        if (x is not LibraryItemViewModel a || y is not LibraryItemViewModel b) return 0;
        int c = GameSummary.CompareByTimeLeft(a.Summary, b.Summary);
        if (c != 0) return c;
        c = Nullable.Compare(b.Game.LastPlayed, a.Game.LastPlayed);
        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
    }
}
