using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using AC.Infrastructure;

namespace AC.App;

/// <summary>
/// The library on the left: the user's games with their progress, filtered by name or to the started ones, in
/// Steam's order or by time left. A game picked in it is opened by the main view.
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly GameService _svc;
    private readonly Dictionary<uint, GameSummary> _summaries;

    public LibraryViewModel(GameService svc)
    {
        _svc = svc;
        _summaries = new Dictionary<uint, GameSummary>(svc.Store.LoadSummaries());
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is LibraryItemViewModel g &&
            (!StartedOnly || g.IsStarted) &&
            (string.IsNullOrWhiteSpace(_filter) || g.Name.Contains(_filter.Trim(), StringComparison.CurrentCultureIgnoreCase));
        _selectedSort = Options.LibrarySorts.FirstOrDefault(s => s.Value == svc.Settings.LibrarySort) ?? Options.LibrarySorts[0];
        ApplySort();
    }

    /// <summary>A game the user picked in the list.</summary>
    public event Action<LibraryItemViewModel>? Picked;

    public ObservableCollection<LibraryItemViewModel> Items { get; } = new();
    public ICollectionView View { get; }

    private Option<LibrarySort> _selectedSort;
    public Option<LibrarySort> SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!Set(ref _selectedSort, value)) return;
            _svc.Settings.LibrarySort = value.Value;
            _svc.SaveSettings();
            ApplySort();
        }
    }

    private bool ByTimeLeft => _selectedSort.Value == LibrarySort.TimeLeft;

    public bool StartedOnly
    {
        get => _svc.Settings.LibraryStartedOnly;
        set
        {
            if (_svc.Settings.LibraryStartedOnly == value) return;
            _svc.Settings.LibraryStartedOnly = value;
            _svc.SaveSettings();
            OnPropertyChanged();
            View.Refresh();
        }
    }

    private void ApplySort()
    {
        foreach (var item in Items) item.ShowLeft = ByTimeLeft;
        if (View is ListCollectionView view) view.CustomSort = ByTimeLeft ? LibraryOrder.ByTimeLeft : null;
    }

    private string _filter = "";
    public string Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) View.Refresh(); }
    }

    private LibraryItemViewModel? _selected;
    public LibraryItemViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value) && value is not null) Picked?.Invoke(value); }
    }

    public string? NameOf(uint appId) => Items.FirstOrDefault(g => g.AppId == appId)?.Name;

    public void Fill(IEnumerable<OwnedGame> games, uint? open)
    {
        Items.Clear();
        foreach (var g in games) Items.Add(new LibraryItemViewModel(g, _summaries.GetValueOrDefault(g.AppId)) { ShowLeft = ByTimeLeft });
        ShowOpen(open);
    }

    /// <summary>
    /// The list marks the open game however it was opened (at start, following the running game, by AppID):
    /// otherwise it stays on the previous one, and a click on that one does not open it again.
    /// </summary>
    public void ShowOpen(uint? appId)
    {
        var item = appId is { } id ? Items.FirstOrDefault(i => i.AppId == id) : null;
        if (ReferenceEquals(item, _selected)) return;
        _selected = item;
        OnPropertyChanged(nameof(Selected));
    }

    /// <summary>The open game's progress: its item shows it, and a list ordered or filtered by it is refreshed.</summary>
    public void UpdateSummary(uint appId, GameSummary summary)
    {
        _summaries[appId] = summary;
        if (Items.FirstOrDefault(i => i.AppId == appId) is not { } item || summary.Equals(item.Summary)) return;
        item.Summary = summary;
        if (ByTimeLeft || StartedOnly) View.Refresh();
    }

    /// <summary>The open game's Steam playtime, read since the library was.</summary>
    public void UpdatePlaytime(uint appId, int minutes)
    {
        if (Items.FirstOrDefault(i => i.AppId == appId) is not { } item || minutes <= item.Game.PlaytimeMinutes) return;
        item.Game.PlaytimeMinutes = minutes;
        item.RefreshTexts();
    }

    public void RefreshTexts()
    {
        foreach (var item in Items) item.RefreshTexts();
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
