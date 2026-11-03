using System.Runtime.Versioning;

namespace AC.Infrastructure;

/// <summary>The user's games from Steam, kept in the data folder, with the progress of those opened in the app.</summary>
[SupportedOSPlatform("windows")]
public sealed class SteamLibrary(JsonStore store, SteamWebApi api, AppSettings settings)
{
    public List<OwnedGame> Cached() => store.LoadLibrary() ?? new List<OwnedGame>();

    public async Task<List<OwnedGame>> RefreshAsync(CancellationToken ct = default)
    {
        var key = settings.RequireApiKey();
        var games = await api.GetOwnedGamesAsync(key, settings.SteamId64, ct);
        games = games.OrderByDescending(g => g.LastPlayed ?? DateTimeOffset.MinValue).ThenBy(g => g.Name).ToList();
        store.SaveLibrary(games);
        return games;
    }

    /// <summary>Progress of every game opened in the app, so the library does not read the game files.</summary>
    public IReadOnlyDictionary<uint, GameSummary> Summaries() => store.LoadSummaries();
}
