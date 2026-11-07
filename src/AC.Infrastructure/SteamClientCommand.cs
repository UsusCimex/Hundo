using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using AC.Core;
using AC.Core.SteamFormats;

namespace AC.Infrastructure;

public enum BridgeAction { Read, Unlock, Lock }

/// <summary>Arguments of the helper process: read|unlock|lock &lt;appid&gt; [api names...] [--lang steam-language] [--ui language].</summary>
public sealed record BridgeCommand(BridgeAction Action, uint AppId, IReadOnlyList<string> Names, string Language, string UiLanguage)
{
    public static BridgeCommand Parse(IReadOnlyList<string> args)
    {
        var rest = new List<string>();
        string lang = "", ui = "";
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--lang" && i + 1 < args.Count) lang = args[++i];
            else if (args[i] == "--ui" && i + 1 < args.Count) ui = args[++i];
            else rest.Add(args[i]);
        }
        if (rest.Count < 2) throw new ArgumentException(Loc.T("client.usage"));
        var action = rest[0].ToLowerInvariant() switch
        {
            "read" => BridgeAction.Read,
            "unlock" => BridgeAction.Unlock,
            "lock" => BridgeAction.Lock,
            _ => throw new ArgumentException(Loc.T("client.badCommand", rest[0])),
        };
        if (!uint.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0)
            throw new ArgumentException(Loc.T("client.badAppId", rest[1]));
        var names = rest.Skip(2).Distinct(StringComparer.Ordinal).ToList();
        if (action != BridgeAction.Read && names.Count == 0) throw new ArgumentException(Loc.T("client.noNames"));
        return new BridgeCommand(action, appId, names, lang, ui);
    }
}

/// <summary>
/// The helper side, run by the app as a child process (<see cref="Flag"/>): connects to the Steam client as the game,
/// reads or changes achievements, prints one JSON line and exits, so Steam stops showing the user in-game.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SteamClientCommand
{
    public const string Flag = "--steam-client";
    private static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(15);

    public static int Run(IReadOnlyList<string> args, Stream stdout)
    {
        var ui = args.SkipWhile(a => a != "--ui").Skip(1).FirstOrDefault();
        Loc.SetLanguage(ui);
        BridgeResultDto result;
        try
        {
            result = Execute(BridgeCommand.Parse(args));
        }
        catch (Exception e)
        {
            result = Fail(e.Message);
        }
        JsonSerializer.Serialize(stdout, result, BridgeClient.Json);
        stdout.WriteByte((byte)'\n');
        stdout.Flush();
        return result.Ok ? 0 : 1;
    }

    private static BridgeResultDto Execute(BridgeCommand cmd)
    {
        using var steam = new SteamClientSession(cmd.AppId);
        if (!steam.OwnsApp(cmd.AppId)) return Fail(Loc.T("client.notOwned"));

        var steamId = steam.SteamId;
        if (!steam.RequestUserStats(steamId)) return Fail(Loc.T("client.statsRequestFailed"));
        switch (steam.WaitForUserStats(cmd.AppId, StatsTimeout))
        {
            case null: return Fail(Loc.T("client.statsTimeout"));
            case 2: return Fail(Loc.T("client.statsNotOwned"));
            case int code and not 1: return Fail(Loc.T("client.statsError", code));
        }

        var lang = cmd.Language.Length > 0 ? cmd.Language : steam.GameLanguage is { Length: > 0 } l ? l : "english";
        var schema = SteamLocal.ReadSchema(cmd.AppId, lang);
        if (schema is null || schema.Count == 0) return Fail(Loc.T("client.noSchema", cmd.AppId));

        var result = new BridgeResultDto { Ok = true, AppId = cmd.AppId, SteamId = steamId, Language = lang };
        if (cmd.Action == BridgeAction.Read)
        {
            result.Achievements = [];
            foreach (var a in schema)
            {
                if (!steam.TryGetAchievement(a.ApiName, out var achieved, out var time)) continue;
                result.Achievements.Add(new BridgeAchievementDto
                {
                    ApiName = a.ApiName,
                    Name = a.Name,
                    Description = a.Description,
                    Hidden = a.Hidden,
                    Protected = a.Protected,
                    Icon = StatsSchemaReader.IconUrl(cmd.AppId, a.Icon),
                    IconGray = StatsSchemaReader.IconUrl(cmd.AppId, a.IconGray),
                    Achieved = achieved,
                    UnlockTime = time?.ToUnixTimeSeconds() ?? 0,
                });
            }
            return result;
        }

        bool unlock = cmd.Action == BridgeAction.Unlock;
        var byName = schema.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
        result.Changes = [];
        foreach (var name in cmd.Names)
        {
            string? error = !byName.TryGetValue(name, out var def) ? Loc.T("client.unknownAchievement")
                : def.Protected ? Loc.T("client.protected")
                : !steam.SetAchievement(name, unlock) ? Loc.T("client.setFailed")
                : null;
            result.Changes.Add(new BridgeChangeDto { ApiName = name, Ok = error is null, Error = error });
        }

        if (result.Changes.Any(c => c.Ok) &&
            StoreError(steam.StoreStats(), () => steam.WaitForStoredStats(cmd.AppId, StatsTimeout)) is { } refused)
        {
            foreach (var c in result.Changes.Where(c => c.Ok))
            {
                c.Ok = false;
                c.Error = refused;
            }
        }
        steam.Flush(TimeSpan.FromSeconds(1));

        result.Ok = result.Changes.All(c => c.Ok);
        if (!result.Ok) result.Error = Loc.T("client.partial");
        return result;
    }

    /// <summary>
    /// Why the changes were not stored; null - stored. Steam takes the request at once and answers once it has stored
    /// the stats or refused them.
    /// </summary>
    /// <param name="sent">Steam took the request.</param>
    /// <param name="answer">Waits for Steam's result code (1 is OK); null - nothing came in time.</param>
    internal static string? StoreError(bool sent, Func<int?> answer) => !sent ? Loc.T("client.storeFailed") : answer() switch
    {
        1 => null,
        null => Loc.T("client.storeTimeout"),
        int code => Loc.T("client.statsError", code),
    };

    private static BridgeResultDto Fail(string error) => new() { Ok = false, Error = error };
}
