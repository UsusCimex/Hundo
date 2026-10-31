using System.Net;
using System.Text.Json;

namespace AC.Infrastructure;

/// <summary>
/// steamhunters.com public JSON: the median time to 100% among its users and Steam's global percentages.
/// It answers when api.steampowered.com turns a network away (a VPN), and it is not behind a browser check.
/// </summary>
public sealed class SteamHunters(HttpClient http)
{
    public const string Name = "steamhunters.com";
    private const string Base = "https://steamhunters.com/api/apps";

    /// <summary>Median minutes to 100%; null - the site has no completions of the game yet.</summary>
    public async Task<double?> GetMedianCompletionMinutesAsync(uint appId, CancellationToken ct = default) =>
        await GetAsync($"{Base}/{appId}", ct) is { } json ? ParseMedian(json) : null;

    /// <summary>Steam's global percentage per API name; empty - the site does not know the game.</summary>
    public async Task<Dictionary<string, double>> GetGlobalPercentagesAsync(uint appId, CancellationToken ct = default) =>
        await GetAsync($"{Base}/{appId}/achievements", ct) is { } json ? ParsePercentages(json) : new();

    /// <summary>The median from the site's answer; it is "null" for a game it does not know (a new one, one without achievements).</summary>
    public static double? ParseMedian(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty("medianCompletionTime", out var v) && v.ValueKind == JsonValueKind.Number &&
               v.GetDouble() > 0 ? v.GetDouble() : null;
    }

    public static Dictionary<string, double> ParsePercentages(string json)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;
        foreach (var a in doc.RootElement.EnumerateArray())
        {
            if (a.TryGetProperty("apiName", out var name) && name.ValueKind == JsonValueKind.String &&
                a.TryGetProperty("steamPercentage", out var pct) && pct.ValueKind == JsonValueKind.Number)
                map[name.GetString()!] = pct.GetDouble();
        }
        return map;
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd($"Hundo/{typeof(SteamHunters).Assembly.GetName().Version?.ToString(3)}");
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }
}
