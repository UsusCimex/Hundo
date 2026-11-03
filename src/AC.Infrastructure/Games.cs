using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

/// <summary>The games in use, one copy each, and the user's changes to them, saved to the game's file at once.</summary>
[SupportedOSPlatform("windows")]
public sealed class Games(JsonStore store, AppSettings settings)
{
    /// <summary>
    /// One copy per game: a second one read from the file while a load still fills the first would lose what is done
    /// to it when that load saves the first.
    /// </summary>
    private readonly Dictionary<uint, WeakReference<GameData>> _inUse = new();

    /// <summary>A game opened before: the copy in use or the one saved; null - never opened.</summary>
    public GameData? Cached(uint appId) => InUse(appId) is { } game ? WithMarkSettings(game) : null;

    /// <summary>The game to load into: the copy in use, the saved one, or a new one.</summary>
    internal GameData Open(uint appId, string? name)
    {
        lock (_inUse) return WithMarkSettings(InUse(appId) ?? Keep(new GameData { AppId = appId, Name = name ?? "" }));
    }

    private GameData? InUse(uint appId)
    {
        lock (_inUse)
        {
            if (_inUse.TryGetValue(appId, out var kept) && kept.TryGetTarget(out var game)) return game;
            return store.LoadGame(appId) is { } read ? Keep(read) : null;
        }
    }

    private GameData Keep(GameData game)
    {
        lock (_inUse) _inUse[game.AppId] = new WeakReference<GameData>(game);
        return game;
    }

    /// <summary>The game as the settings count its marks (<see cref="GameData.CountManualMarks"/>, <see cref="GameData.MarkByTime"/>).</summary>
    public GameData WithMarkSettings(GameData game)
    {
        game.CountManualMarks = settings.ManualUnlocks;
        game.MarkByTime = settings.ManualUnlocks && settings.AutoMark;
        return game;
    }

    public void Save(GameData game) => store.SaveGame(game);

    /// <summary>Re-derives automatic types and groups, so group names follow the interface language.</summary>
    public void Relabel(GameData game)
    {
        GameLogic.AutoClassify(game);
        Save(game);
    }

    public void SetNote(GameData game, string apiName, string note)
    {
        game.StateOf(apiName).Note = note;
        Save(game);
    }

    public void SetOffOverlay(GameData game, string apiName, bool off)
    {
        game.StateOf(apiName).OffOverlay = off;
        Save(game);
    }

    public void SetKind(GameData game, string apiName, AchievementKind kind)
    {
        GameLogic.SetKind(game, apiName, kind);
        Save(game);
    }

    public void SetGroup(GameData game, string apiName, string group)
    {
        GameLogic.SetGroup(game, apiName, group);
        Save(game);
    }

    public void SetManualEstimate(GameData game, string apiName, double? minutes)
    {
        if (minutes is > 0) game.ManualEstimates[apiName] = minutes.Value;
        else game.ManualEstimates.Remove(apiName);
        Save(game);
    }

    public void SetCompletionMinutes(GameData game, double? minutes, string source)
    {
        game.CompletionMinutes = minutes is > 0 ? minutes : null;
        game.CompletionSource = game.CompletionMinutes is null ? "" : source;
        TimeEstimator.EstimateFromGlobalPercent(game);
        Save(game);
    }

    /// <summary>Own "actually played" time (null - from Steam) and Steam hours to ignore.</summary>
    public void SetPlaytime(GameData game, double? overrideMinutes, double offsetMinutes)
    {
        overrideMinutes = overrideMinutes is >= 0 ? overrideMinutes : null;
        if (overrideMinutes != game.PlaytimeOverrideMinutes) game.PlaytimeOverrideAt = overrideMinutes is null ? null : DateTimeOffset.Now;
        game.PlaytimeOverrideMinutes = overrideMinutes;
        game.PlaytimeOffsetMinutes = Math.Max(0, offsetMinutes);
        Save(game);
    }

    public int ImportTags(GameData game, string json)
    {
        var n = TagPack.FromJson(json).ApplyTo(game);
        TimeEstimator.EstimateFromGlobalPercent(game);
        Save(game);
        return n;
    }
}
