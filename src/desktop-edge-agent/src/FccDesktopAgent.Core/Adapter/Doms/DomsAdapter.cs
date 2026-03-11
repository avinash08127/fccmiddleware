using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Doms;

/// <summary>
/// DOMS-protocol FCC adapter. Communicates with the Forecourt Controller
/// over the station LAN using the DOMS HTTP API.
/// </summary>
public sealed class DomsAdapter : IFccAdapter
{
    private readonly HttpClient _http;
    private readonly ILogger<DomsAdapter> _logger;

    public DomsAdapter(HttpClient http, ILogger<DomsAdapter> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task<CanonicalTransaction> NormalizeAsync(RawPayloadEnvelope rawPayload, CancellationToken ct)
        => throw new NotImplementedException("DEA-1.x: Implement DOMS transaction normalization");

    public Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct)
        => throw new NotImplementedException("DEA-1.x: Implement DOMS pre-auth");

    public Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct)
        => throw new NotImplementedException("DEA-1.x: Implement DOMS pump-status fetch");

    public async Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DOMS heartbeat failed");
            return false;
        }
    }

    public Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct)
        => throw new NotImplementedException("DEA-1.x: Implement DOMS transaction fetch");
}
