using System.Globalization;

namespace AC.Core.SteamFormats;

/// <summary>A stat's value within the range an achievement needs.</summary>
public readonly record struct StatProgress(double Value, double Min, double Max)
{
    public double Fraction => Math.Clamp((Value - Min) / (Max - Min), 0, 1);
}

/// <summary>
/// Progress of achievements that count something. The schema ties such an achievement to a stat by name
/// (&lt;appid&gt;/stats/&lt;id&gt;/bits/&lt;n&gt;/progress: min_val, max_val, value/operand1), and the player's
/// UserGameStats_&lt;account&gt;_&lt;appid&gt;.bin holds the stat values by id (cache/&lt;id&gt;/data), those of
/// float stats as their bits.
/// </summary>
public static class StatsProgressReader
{
    public static Dictionary<string, StatProgress> Read(KvNode schema, KvNode userStats, uint appId)
    {
        var stats = schema[appId.ToString(CultureInfo.InvariantCulture)]["stats"].Children;
        var byName = new Dictionary<string, KvNode>(StringComparer.Ordinal);
        foreach (var stat in stats)
            if (stat["name"].Text() is { Length: > 0 } name) byName[name] = stat;

        var progress = new Dictionary<string, StatProgress>(StringComparer.Ordinal);
        foreach (var bit in stats.SelectMany(s => s["bits"].Children))
        {
            var range = bit["progress"];
            var value = range["value"];
            if (!value["operation"].Text().Equals("statvalue", StringComparison.OrdinalIgnoreCase) ||
                !byName.TryGetValue(value["operand1"].Text(), out var stat))
                continue;
            double min = Number(range["min_val"]), max = Number(range["max_val"]);
            if (max <= min || bit["name"].Text() is not { Length: > 0 } apiName) continue;
            // Steam keeps only the stats that left their default.
            var data = userStats["cache"][stat.Name]["data"];
            double current = !data.Exists ? Number(stat["default"]) : IsFloat(stat) ? FloatOf(data) : data.Number();
            progress[apiName] = new StatProgress(current, min, max);
        }
        return progress;
    }

    private static bool IsFloat(KvNode stat) => stat["type"].Text().ToUpperInvariant() is "FLOAT" or "AVGRATE" or "2" or "3";

    private static double FloatOf(KvNode data) => data.Value switch
    {
        float f => f,
        int bits => BitConverter.Int32BitsToSingle(bits),
        _ => data.Number(),
    };

    private static double Number(KvNode node) => node.Value switch
    {
        float f => f,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => node.Number(),
    };
}
