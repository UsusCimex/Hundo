using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace AC.Core;

public enum ExportScope
{
    /// <summary>All achievements.</summary>
    All = 0,
    /// <summary>Only "done but not counted" - the ones still to get counted.</summary>
    ManualOnly = 1,
}

/// <summary>Export to CSV (opens in Excel) and JSON.</summary>
public static class Exporter
{
    public static IEnumerable<AchievementRow> Select(GameData game, ExportScope scope) =>
        GameLogic.BuildRows(game, Percentile.P50)
            .Where(r => scope == ExportScope.All || r.Status == EffectiveStatus.ManualDone);

    /// <summary>
    /// CSV with a ";" separator (Russian Excel) and a UTF-8 BOM to avoid mojibake. Fractions go with a decimal comma
    /// unless the separator is a comma: with a ";" one Excel reads "5.3" as the 5th of March.
    /// </summary>
    public static byte[] ToCsv(GameData game, ExportScope scope, char separator = ';')
    {
        var decimals = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        if (separator != ',') decimals.NumberDecimalSeparator = ",";
        var sb = new StringBuilder();
        string[] header = { "api_name", "name", "status", "date", "kind", "group", "global_pct", "note" };
        sb.AppendLine(string.Join(separator, header));
        foreach (var r in Select(game, scope))
        {
            var fields = new[]
            {
                r.ApiName,
                r.Def.Name,
                StatusCode(r.Status),
                r.UnlockTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "",
                r.Kind.ToString().ToLowerInvariant(),
                r.Group,
                r.Def.Percent?.ToString("0.0", decimals) ?? "",
                r.Note,
            };
            sb.AppendLine(string.Join(separator, fields.Select(f => Escape(f, separator))));
        }
        var bom = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        return bom.Concat(body).ToArray();
    }

    public static string ToJson(GameData game, ExportScope scope)
    {
        var payload = new
        {
            appid = game.AppId,
            game = game.Name,
            exported_at = DateTimeOffset.Now,
            achievements = Select(game, scope).Select(r => new
            {
                api_name = r.ApiName,
                name = r.Def.Name,
                status = StatusCode(r.Status),
                date = r.UnlockTime,
                kind = r.Kind.ToString().ToLowerInvariant(),
                group = r.Group,
                global_pct = r.Def.Percent,
                note = r.Note,
            }),
        };
        return JsonSerializer.Serialize(payload, JsonOptions.Pretty);
    }

    public static string StatusCode(EffectiveStatus s) => s switch
    {
        EffectiveStatus.Unlocked => "unlocked",
        EffectiveStatus.ManualDone => "done_not_counted",
        _ => "locked",
    };

    private static string Escape(string value, char sep)
    {
        // Excel runs a cell that starts like a formula; names come from game developers, notes from shared tags files.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        if (value.IndexOfAny(new[] { sep, '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
