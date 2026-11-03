using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

public sealed record MarkReport(bool SteamWritten, string? Error);

/// <summary>
/// "Done" marks and what they do in Steam by the write mode: nothing, a write through the Steam client helper, or the
/// queue. Every write goes to the write log.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MarkService(BridgeClient bridge, JsonStore store, Games games, AppSettings settings)
{
    /// <summary>
    /// Marks the achievements and does in Steam what the write mode says - nothing, write them now, or queue them.
    /// Ones unlocked in Steam stay as they are; protected ones are only marked.
    /// </summary>
    public async Task<MarkReport> CompleteAsync(GameData game, IReadOnlyCollection<string> apiNames, CancellationToken ct = default)
    {
        if (MarksOff() is { } off) return off;
        var now = DateTimeOffset.Now;
        var defs = game.Achievements.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        var toSteam = new List<string>();
        var skipped = new List<string>();
        foreach (var n in apiNames.Distinct(StringComparer.Ordinal))
        {
            var s = game.StateOf(n);
            if (s.SteamUnlocked) continue;
            if (!s.ManualDone) (s.ManualDone, s.ManualAt) = (true, now);
            if (settings.WriteMode == WriteMode.MarkOnly || !defs.TryGetValue(n, out var def)) continue;
            if (def.Protected) skipped.Add(game.NameOf(n));
            else toSteam.Add(n);
        }
        // Saved before Steam is asked: the marks are not lost if the helper fails.
        games.Save(game);

        var report = new MarkReport(false, null);
        if (settings.WriteMode == WriteMode.Queue) game.PendingUnlocks.AddRange(toSteam.Except(game.PendingUnlocks));
        else if (toSteam.Count > 0) report = await WriteAsync(game, toSteam, unlock: true, ct);
        games.Save(game);
        if (skipped.Count == 0) return report;
        var protectedNote = Loc.T("error.protected", string.Join(", ", skipped));
        return report with { Error = report.Error is null ? protectedNote : report.Error + "; " + protectedNote };
    }

    /// <summary>Takes a "Done" mark back, and the achievement out of the queue. Steam is not asked (<see cref="RevertAsync"/> is).</summary>
    public void Uncomplete(GameData game, string apiName)
    {
        var s = game.StateOf(apiName);
        (s.ManualDone, s.ManualAt) = (false, null);
        game.PendingUnlocks.Remove(apiName);
        games.Save(game);
    }

    /// <summary>Sends the queued marks to Steam.</summary>
    public async Task<MarkReport> FlushQueueAsync(GameData game, CancellationToken ct = default)
    {
        if (MarksOff() is { } off) return off;
        var names = game.PendingUnlocks.Where(n => !game.StateOf(n).SteamUnlocked).ToList();
        if (names.Count == 0) return new MarkReport(false, null);
        var r = await WriteAsync(game, names, unlock: true, ct);
        games.Save(game);
        return r;
    }

    /// <summary>
    /// Locks an achievement in Steam and takes back its mark: the app marks what it writes. A mark by time stays known,
    /// so the achievement is not marked by time again.
    /// </summary>
    public async Task<MarkReport> RevertAsync(GameData game, string apiName, CancellationToken ct = default)
    {
        if (MarksOff() is { } off) return off;
        var r = await WriteAsync(game, new[] { apiName }, unlock: false, ct);
        if (r.SteamWritten)
        {
            var s = game.StateOf(apiName);
            (s.ManualDone, s.ManualAt) = (false, null);
        }
        games.Save(game);
        return r;
    }

    /// <summary>Whether the app unlocked the achievement in Steam and did not relock it.</summary>
    public bool WasWrittenByApp(uint appId, string apiName)
    {
        var last = store.ReadLog(appId).LastOrDefault(e => e.ApiName == apiName && e.Ok);
        return last?.Action == "unlock";
    }

    private MarkReport? MarksOff() => settings.ManualUnlocks ? null : new MarkReport(false, Loc.T("error.manualOff"));

    private async Task<MarkReport> WriteAsync(GameData game, IReadOnlyList<string> names, bool unlock, CancellationToken ct)
    {
        if (BridgeClient.SignedInElsewhere(settings.SteamId64) is { } other) return new MarkReport(false, other);
        var r = unlock ? await bridge.UnlockAsync(game.AppId, names, ct) : await bridge.LockAsync(game.AppId, names, ct);
        var now = DateTimeOffset.Now;
        var action = unlock ? "unlock" : "lock";

        if (r.Changes is null)
        {
            Log(names.Select(n => new UnlockLogEntry { At = now, AppId = game.AppId, ApiName = n, Action = action, Ok = false, Error = r.Error }));
            return new MarkReport(false, r.Error ?? Loc.T("error.bridgeNoResult"));
        }

        foreach (var c in r.Changes.Where(c => c.Ok))
        {
            var s = game.StateOf(c.ApiName);
            s.SteamUnlocked = unlock;
            s.SteamUnlockTime = unlock ? now : null;
            s.WrittenAt = now;
            game.PendingUnlocks.Remove(c.ApiName);
        }
        Log(r.Changes.Select(c => new UnlockLogEntry { At = now, AppId = game.AppId, ApiName = c.ApiName, Action = action, Ok = c.Ok, Error = c.Error }));
        var failed = r.Changes.Where(c => !c.Ok).ToList();
        return new MarkReport(r.Changes.Any(c => c.Ok),
            failed.Count == 0 ? null : string.Join("; ", failed.Select(f => $"{game.NameOf(f.ApiName)}: {f.Error}")));
    }

    /// <summary>The write log is the user's record: a line that cannot be written must not undo what Steam did.</summary>
    private void Log(IEnumerable<UnlockLogEntry> entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                store.AppendLog(entry);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Info($"{entry.AppId}: write log, {entry.Action} {entry.ApiName} ok={entry.Ok}: {e.Message}");
            }
        }
    }
}
