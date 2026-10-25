namespace AC.App;

/// <summary>A heading over a group of the list: a stage or a group of the plan, or a day of unlocks.</summary>
public sealed class ListSection(string title) : ObservableObject
{
    /// <summary>For rows with no heading of their own inside a stage: a plan group without a name.</summary>
    public static ListSection None { get; } = new("");

    public string Title { get; } = title;

    private string _count = "";
    /// <summary>How many rows it heads, after the title; empty - not shown.</summary>
    public string Count { get => _count; set => Set(ref _count, value); }

    private string _note = "";
    /// <summary>On the right: "up to 4 h", the hours of the group's last achievement, or "+3" unlocked on a day.</summary>
    public string Note { get => _note; set => Set(ref _note, value); }
}
