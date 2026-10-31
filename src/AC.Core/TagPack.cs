using System.Text.Json;

namespace AC.Core;

/// <summary>
/// A game's tags file: kinds (missable lists among them), groups, manual estimates and notes (a short guide per
/// achievement). It can be put together from a guide, saved and moved to another computer or shared with a friend.
/// </summary>
public sealed class TagPack
{
    public int Version { get; set; } = 1;
    public uint AppId { get; set; }
    public string Game { get; set; } = "";
    public double? CompletionMinutes { get; set; }
    public List<TagPackItem> Items { get; set; } = new();

    public static TagPack From(GameData game)
    {
        var pack = new TagPack { AppId = game.AppId, Game = game.Name, CompletionMinutes = game.CompletionMinutes };
        foreach (var def in game.Achievements)
        {
            game.Tags.TryGetValue(def.ApiName, out var t);
            bool manualKind = t?.Source == KindSource.Manual;
            // automatic groups ("in chapter 3") are detected again on the receiving side
            bool hasGroup = t is { GroupIsManual: true } && !string.IsNullOrWhiteSpace(t.Group);
            bool hasEst = game.ManualEstimates.TryGetValue(def.ApiName, out var est);
            var note = game.States.GetValueOrDefault(def.ApiName)?.Note.Trim() ?? "";
            if (!manualKind && !hasGroup && !hasEst && note.Length == 0) continue;
            pack.Items.Add(new TagPackItem
            {
                ApiName = def.ApiName,
                Kind = manualKind ? t!.Kind : null,
                Group = hasGroup ? t!.Group : null,
                EstimateMinutes = hasEst ? est : null,
                Note = note.Length > 0 ? note : null,
            });
        }
        return pack;
    }

    /// <summary>
    /// Applies the tags. Returns the number of applied items (unknown api names are skipped).
    /// A file of another game is refused: generic api names (ACH_1) would match.
    /// </summary>
    public int ApplyTo(GameData game)
    {
        if (AppId != 0 && AppId != game.AppId)
            throw new FormatException(Loc.T("error.tagsOtherGame", string.IsNullOrWhiteSpace(Game) ? AppId.ToString() : Game));
        var known = game.Achievements.Select(a => a.ApiName).ToHashSet(StringComparer.Ordinal);
        int applied = 0;
        foreach (var item in Items)
        {
            if (!known.Contains(item.ApiName)) continue;
            var tag = game.TagOf(item.ApiName);
            if (item.Kind is { } kind && Enum.IsDefined(kind))
            {
                tag.Kind = kind;
                tag.Source = KindSource.Manual;
            }
            if (item.Group is not null)
            {
                tag.Group = item.Group;
                tag.GroupSource = KindSource.Manual;
            }
            if (item.EstimateMinutes is { } est && TimeEstimator.IsValidMinutes(est)) game.ManualEstimates[item.ApiName] = est;
            if (item.Note?.Trim() is { Length: > 0 } note)
            {
                // An own note stays: the imported one goes after it.
                var state = game.StateOf(item.ApiName);
                var own = state.Note.Trim();
                if (own.Length == 0) state.Note = note;
                else if (!own.Contains(note, StringComparison.Ordinal)) state.Note = own + "\n\n" + note;
            }
            applied++;
        }
        if (CompletionMinutes is { } total && TimeEstimator.IsValidMinutes(total) && game.CompletionMinutes is null)
            game.CompletionMinutes = total;
        return applied;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions.Pretty);

    public static TagPack FromJson(string json)
    {
        var pack = JsonSerializer.Deserialize<TagPack>(json, JsonOptions.Pretty) ?? throw new FormatException(Loc.T("error.emptyTags"));
        // A file made by hand may have null where a list, an item or a name is expected.
        pack.Items = pack.Items?.Where(i => i is { ApiName: not null }).ToList() ?? new();
        pack.Game ??= "";
        return pack;
    }
}

public sealed class TagPackItem
{
    public string ApiName { get; set; } = "";
    public AchievementKind? Kind { get; set; }
    public string? Group { get; set; }
    public double? EstimateMinutes { get; set; }
    public string? Note { get; set; }
}
