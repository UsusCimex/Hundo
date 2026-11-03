using System.Runtime.Versioning;
using AC.Core;

namespace AC.Infrastructure;

/// <summary>
/// Own statistics from a player sample, for games nobody has percentages or times of: the achievements of the game's
/// review authors with public profiles give percentages and the time to each achievement.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SampleCollector(SteamWebApi api, Games games, AppSettings settings)
{
    /// <summary>Reads the players; ~100 requests, usually 20-60 seconds. Only computes, <see cref="Apply"/> applies.</summary>
    public async Task<SampleResult> CollectAsync(GameData game, IProgress<string>? progress = null,
        int maxAuthors = 300, int targetPlayers = 80, CancellationToken ct = default)
    {
        var key = settings.RequireApiKey();
        progress?.Report(Loc.T("sample.searching"));
        var authors = await api.GetReviewAuthorsAsync(game.AppId, maxAuthors, ct);
        authors = authors.Where(a => a.SteamId != settings.SteamId64).ToList();
        if (authors.Count == 0) throw new SteamApiException(Loc.T("sample.noReviews"));

        var samples = new List<PlayerSample>();
        SteamApiException? fatal = null;
        int checkedCount = 0;
        using var gate = new SemaphoreSlim(4);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = authors.Select(async a =>
        {
            try { await gate.WaitAsync(stop.Token); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (stop.IsCancellationRequested) return;
                var list = await api.GetPlayerAchievementsAsync(key, a.SteamId, game.AppId, stop.Token);
                var unlocks = list.Where(x => x.Achieved && x.UnlockTime is not null)
                                  .ToDictionary(x => x.ApiName, x => x.UnlockTime!.Value, StringComparer.Ordinal);
                var sample = new PlayerSample { SteamId = a.SteamId, Unlocks = unlocks };
                if (a.ReviewedAt is { } rv && a.PlaytimeAtReview > 0) sample.Anchors.Add((rv, a.PlaytimeAtReview));
                if (a.LastPlayed is { } lp && a.PlaytimeForever > 0) sample.Anchors.Add((lp, a.PlaytimeForever));
                bool enough;
                lock (samples)
                {
                    samples.Add(sample);
                    enough = samples.Count >= targetPlayers;
                }
                if (enough) stop.Cancel();
            }
            catch (SteamApiException e) when (e.Fatal)
            {
                // A blocked network or a rejected key fails every player the same way.
                fatal ??= e;
                stop.Cancel();
            }
            catch (Exception e) when (HangGuard.IsUnanswered(e))
            {
                // So does a Web API that stopped answering: skipped, the players after would leave a sample of a few.
                fatal ??= new SteamApiException(e.Message);
                stop.Cancel();
            }
            catch (Exception e) when (e is SteamApiException or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                // private profile or failure - skip the player
            }
            finally
            {
                var c = Interlocked.Increment(ref checkedCount);
                if (c % 10 == 0) progress?.Report(Loc.T("sample.progress", c, authors.Count, samples.Count));
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        // The players read before the API stopped answering make a sample when they are half of those wanted.
        if (fatal != null && samples.Count < targetPlayers / 2) throw fatal;
        ct.ThrowIfCancellationRequested();

        if (samples.Count == 0)
            throw new SteamApiException(Loc.Plural("sample.allPrivate", authors.Count));

        // Only compute: the caller applies it to the current game object (it may have been reloaded meanwhile).
        return SampleStats.Compute(samples, game.Achievements.Select(a => a.ApiName).ToList());
    }

    public void Apply(GameData game, SampleResult r)
    {
        SampleStats.Apply(game, r, DateTimeOffset.Now);
        TimeEstimator.EstimateFromGlobalPercent(game);
        games.Save(game);
    }
}
