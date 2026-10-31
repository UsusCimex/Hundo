using System.Collections.Concurrent;
using AC.Core;

namespace AC.Infrastructure;

/// <summary>
/// A host that did not answer in time is skipped for a while: opening a game asks it several times (schema,
/// percentages, status, playtime), and every game opened after would wait for it again.
/// </summary>
public sealed class HangGuard(HttpMessageHandler inner, TimeSpan timeout, TimeSpan rest) : DelegatingHandler(inner)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _restUntil = new(StringComparer.OrdinalIgnoreCase);

    public HangGuard() : this(new HttpClientHandler(), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(5)) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host ?? "";
        if (_restUntil.TryGetValue(host, out var until) && DateTimeOffset.UtcNow < until)
            throw new HttpRequestException(Loc.T("error.hostResting", host));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await base.SendAsync(request, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _restUntil[host] = DateTimeOffset.UtcNow + rest;
            throw new TaskCanceledException(Loc.T("error.hostTimeout", host, timeout.TotalSeconds));
        }
    }
}
