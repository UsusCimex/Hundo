using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AC.Core;

namespace AC.Infrastructure;

public sealed class BridgeAchievementDto
{
    public string ApiName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Hidden { get; set; }
    public bool Protected { get; set; }
    public string Icon { get; set; } = "";
    public string IconGray { get; set; } = "";
    public bool Achieved { get; set; }
    public long UnlockTime { get; set; }
}

public sealed class BridgeChangeDto
{
    public string ApiName { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

public sealed class BridgeResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public uint AppId { get; set; }
    public ulong SteamId { get; set; }
    public string? Language { get; set; }
    public List<BridgeAchievementDto>? Achievements { get; set; }
    public List<BridgeChangeDto>? Changes { get; set; }
}

/// <summary>
/// Runs the app's own exe as the Steam helper (<see cref="SteamClientCommand"/>) and reads its answer. A separate
/// short-lived process, because Steam takes the process that connects for the game and shows the user in it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BridgeClient
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    // Until when the helper uses a game: Steam names it as running meanwhile, and a moment after.
    private static readonly ConcurrentDictionary<uint, DateTimeOffset> InUseUntil = new();
    private static readonly TimeSpan NamedAfterExit = TimeSpan.FromSeconds(10);

    public string ExePath { get; }
    private readonly bool _standIn;

    /// <summary>How long the helper may take: connecting, reading or writing, and letting go of Steam.</summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <param name="exePath">
    /// A stand-in that prints the helper's answer, for tests and checks of writes without Steam (also AC_STEAM_HELPER);
    /// null - the app's own exe.
    /// </param>
    public BridgeClient(string? exePath = null)
    {
        exePath ??= Environment.GetEnvironmentVariable("AC_STEAM_HELPER") is { Length: > 0 } standIn ? standIn : null;
        _standIn = exePath is not null;
        ExePath = exePath ?? Environment.ProcessPath ?? "";
    }

    /// <summary>
    /// The helper reads and writes as whoever is signed in to the Steam client: another account than the user's would
    /// get the marks, and its status would mix into the user's. null - the same, or nothing to compare.
    /// </summary>
    public static string? SignedInElsewhere(ulong steamId64) =>
        OtherAccount(SteamLocal.SignedInAccountId(), steamId64) ? Loc.T("error.otherAccount") : null;

    /// <summary>The Steam client is signed in to an account, and not to this one; false - the same, or unknown.</summary>
    internal static bool OtherAccount(uint signedIn, ulong steamId64) =>
        signedIn != 0 && steamId64 != 0 && signedIn != SteamLocal.AccountId(steamId64);

    /// <summary>The helper can run; AC_NO_STEAM_CLIENT=1 turns the real one off (scripted checks must not touch Steam).</summary>
    public bool Exists => File.Exists(ExePath) && (_standIn || Environment.GetEnvironmentVariable("AC_NO_STEAM_CLIENT") != "1");

    /// <summary>The helper uses the game now or did a moment ago, so Steam names it as running.</summary>
    public static bool InUse(uint appId) => InUseUntil.TryGetValue(appId, out var until) && DateTimeOffset.UtcNow < until;

    public Task<BridgeResultDto> ReadAsync(uint appId, string language, CancellationToken ct = default) =>
        RunAsync(appId, ["read", appId.ToString(), "--lang", language], ct);

    public Task<BridgeResultDto> UnlockAsync(uint appId, IEnumerable<string> apiNames, CancellationToken ct = default) =>
        RunAsync(appId, ["unlock", appId.ToString(), .. apiNames], ct);

    public Task<BridgeResultDto> LockAsync(uint appId, IEnumerable<string> apiNames, CancellationToken ct = default) =>
        RunAsync(appId, ["lock", appId.ToString(), .. apiNames], ct);

    private async Task<BridgeResultDto> RunAsync(uint appId, string[] args, CancellationToken ct)
    {
        if (!Exists)
            return new BridgeResultDto { Ok = false, Error = Loc.T("error.bridgeMissing", ExePath) };

        // Steam takes the process for a game: no point in two helpers at once.
        await OneAtATime.WaitAsync(ct);
        InUseUntil[appId] = DateTimeOffset.MaxValue;
        try
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in (string[])[SteamClientCommand.Flag, .. args, "--ui", Loc.Language]) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("error.bridgeStart"));
            var stdoutTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
            bool exited = true;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(Timeout);
                try
                {
                    await p.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    exited = false;
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
            }

            // A helper that answered and then hung while letting go of Steam has done its work all the same.
            var stdout = (await Printed(stdoutTask)).Trim();
            var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (line.Length == 0)
            {
                if (!exited) return new BridgeResultDto { Ok = false, Error = Loc.T("error.bridgeTimeout") };
                var err = (await Printed(stderrTask)).Trim();
                return new BridgeResultDto { Ok = false, Error = err.Length > 0 ? Loc.T("error.bridgeEmptyWith", err) : Loc.T("error.bridgeEmpty") };
            }
            return JsonSerializer.Deserialize<BridgeResultDto>(line, Json)
                   ?? new BridgeResultDto { Ok = false, Error = Loc.T("error.bridgeParse") };
        }
        catch (JsonException e)
        {
            return new BridgeResultDto { Ok = false, Error = Loc.T("error.bridgeJson", e.Message) };
        }
        finally
        {
            InUseUntil[appId] = DateTimeOffset.UtcNow + NamedAfterExit;
            OneAtATime.Release();
        }
    }

    /// <summary>What the helper printed: its output ends with it, or soon after it is killed.</summary>
    private static async Task<string> Printed(Task<string> output)
    {
        try
        {
            return await output.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            return "";
        }
    }
}
