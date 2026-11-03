using System.Collections.ObjectModel;
using AC.Core;
using AC.Infrastructure;

namespace AC.App;

/// <summary>The overlay's lists: the locked missables, and the nearest achievements by time.</summary>
public sealed class OverlayLists
{
    private readonly MainViewModel _main;
    private readonly AppServices _svc;

    public OverlayLists(MainViewModel main, AppServices svc)
    {
        _main = main;
        _svc = svc;
        MarkCommand = new AsyncCommand(p => p is AchievementRowViewModel { IsUnlocked: false } r ? main.CompleteAsync([r.ApiName], inGame: true) : Task.CompletedTask,
            main.OnError, _ => main.ManualUnlocks);
        HideCommand = new RelayCommand(p => { if (p is AchievementRowViewModel r) r.InOverlay = false; });
    }

    public ObservableCollection<AchievementRowViewModel> Missable { get; } = new();
    public ObservableCollection<AchievementRowViewModel> Nearest { get; } = new();
    /// <summary>"Done" from the overlay. Parameter: the row.</summary>
    public AsyncCommand MarkCommand { get; }
    public RelayCommand HideCommand { get; }

    public void Update(IReadOnlyList<AchievementRow> all)
    {
        // The ones the player has gone past go last: on the "from the start" hours they would come first.
        var open = all.Where(r => !r.IsDone && !r.OffOverlay).ToList();
        Missable.Clear();
        foreach (var r in open.Where(r => r.Kind == AchievementKind.Missable)
                              .OrderBy(r => r.Passed)
                              .ThenBy(r => _main.OrderHoursOf(r) ?? double.MaxValue).Take(6))
            Missable.Add(new AchievementRowViewModel(_main, r));

        Nearest.Clear();
        foreach (var r in open.Where(r => r.Kind != AchievementKind.Missable)
                              .OrderBy(r => r.Passed)
                              .ThenBy(r => _main.OrderHoursOf(r) is null)
                              .ThenBy(_main.OrderHoursOf)
                              .ThenByDescending(r => r.Def.Percent ?? -1)
                              .Take(Math.Max(1, _svc.Settings.OverlayNearestCount)))
            Nearest.Add(new AchievementRowViewModel(_main, r));
        if (Nearest.Count > 0) Nearest[0].TipInOverlay = true;
    }
}
