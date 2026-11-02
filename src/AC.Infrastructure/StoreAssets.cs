using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AC.Infrastructure;

/// <summary>
/// Store art of newer apps lives in hashed folders (store_item_assets/steam/apps/&lt;appid&gt;/&lt;hash&gt;/header.jpg),
/// and the plain path answers 404. The real paths come from IStoreBrowseService (no key); when the API is blocked,
/// the header still comes from the store's appdetails.
/// </summary>
public static partial class StoreAssets
{
    private const string Root = "https://shared.akamai.steamstatic.com/store_item_assets/";

    private static readonly Dictionary<string, string> AssetOfFile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["header.jpg"] = "header",
        ["library_hero.jpg"] = "library_hero",
        ["library_600x900.jpg"] = "library_capsule",
        ["capsule_616x353.jpg"] = "main_capsule",
        ["capsule_231x87.jpg"] = "small_capsule",
    };

    private static readonly Dictionary<uint, Task<Dictionary<string, string>?>> Apps = new();
    private static readonly Lock Gate = new();

    /// <summary>Where the picture behind a plain store URL really is; null - not a store URL or not found.</summary>
    public static async Task<string?> ResolveAsync(HttpClient http, string url, CancellationToken ct = default)
    {
        var m = Plain().Match(url);
        if (!m.Success || !AssetOfFile.TryGetValue(m.Groups["file"].Value, out var asset)) return null;
        var appId = uint.Parse(m.Groups["id"].Value);
        Task<Dictionary<string, string>?> task;
        lock (Gate)
        {
            if (!Apps.TryGetValue(appId, out task!))
                Apps[appId] = task = LoadAsync(http, appId, ct);
        }
        var assets = await task;
        // The store's answer is kept; a lookup that got none (no network) is made again for the next picture.
        if (assets is null)
            lock (Gate)
                if (Apps.TryGetValue(appId, out var kept) && kept == task) Apps.Remove(appId);
        return assets?.GetValueOrDefault(asset);
    }

    /// <summary>The app's assets by name; null - neither the API nor the store answered.</summary>
    private static async Task<Dictionary<string, string>?> LoadAsync(HttpClient http, uint appId, CancellationToken ct)
    {
        try
        {
            var input = $"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},\"data_request\":{{\"include_assets\":true}}}}";
            var json = await http.GetStringAsync(
                $"https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json={Uri.EscapeDataString(input)}", ct);
            var assets = ParseItems(json);
            if (assets.Count > 0) return assets;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { }
        try
        {
            var json = await http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic", ct);
            return ParseDetailsHeader(json, appId) is { } header ? new() { ["header"] = header } : new();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Asset name to full URL from an IStoreBrowseService/GetItems answer.</summary>
    public static Dictionary<string, string> ParseItems(string json)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var resp) || !resp.TryGetProperty("store_items", out var items))
            return map;
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("assets", out var assets) || !assets.TryGetProperty("asset_url_format", out var format))
                continue;
            foreach (var p in assets.EnumerateObject())
                if (p.Name != "asset_url_format" && p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = Root + format.GetString()!.Replace("${FILENAME}", p.Value.GetString());
        }
        return map;
    }

    public static string? ParseDetailsHeader(string json, uint appId)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(appId.ToString(), out var app) && app.TryGetProperty("data", out var data) &&
               data.TryGetProperty("header_image", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
    }

    public static bool IsNotFound(Exception e) => e is HttpRequestException { StatusCode: HttpStatusCode.NotFound };

    [GeneratedRegex(@"^https://[^/]+/store_item_assets/steam/apps/(?<id>\d+)/(?<file>[^/?]+)$")]
    private static partial Regex Plain();
}
