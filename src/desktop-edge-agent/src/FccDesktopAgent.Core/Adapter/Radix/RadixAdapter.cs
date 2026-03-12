namespace FccDesktopAgent.Core.Adapter.Radix;

using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.Extensions.Logging;

/// <summary>
/// Radix FCC adapter — HTTP/XML stateless protocol on dual ports.
///
/// Communicates with the FCC over station LAN using HTTP POST with XML bodies:
/// <list type="bullet">
///   <item><b>Auth port P</b> (from config AuthPort) — external authorization (pre-auth)</item>
///   <item><b>Transaction port P+1</b> — transaction management, products, day close, ATG, CSR</item>
///   <item><b>Signing</b> — SHA-1 hash of XML body + shared secret password</item>
///   <item><b>Heartbeat</b> — CMD_CODE=55 (product/price read) — no dedicated endpoint</item>
///   <item><b>Fetch</b> — FIFO drain loop: CMD_CODE=10 (request) then CMD_CODE=201 (ACK) then repeat</item>
///   <item><b>Pre-auth</b> — AUTH_DATA XML to auth port P</item>
///   <item><b>Pump status</b> — Not supported by Radix protocol</item>
/// </list>
///
/// Stub implementation — methods return defaults or throw <see cref="NotImplementedException"/>.
/// Full implementation follows RX-2.x tasks.
/// </summary>
public sealed class RadixAdapter : IFccAdapter
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly FccConnectionConfig _config;
    private readonly ILogger<RadixAdapter> _logger;

    /// <summary>Transaction management port = AuthPort + 1. Falls back to BaseUrl port + 1 if AuthPort not set.</summary>
    private int TransactionPort => (_config.AuthPort ?? 0) + 1;

    /// <summary>Shared secret for SHA-1 message signing.</summary>
    private string SharedSecret => _config.SharedSecret ?? string.Empty;

    /// <summary>Unique Station Number sent as USN-Code HTTP header.</summary>
    private int UsnCode => _config.UsnCode ?? 0;

    public RadixAdapter(IHttpClientFactory httpFactory, FccConnectionConfig config, ILogger<RadixAdapter> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>Not yet implemented — awaiting RX-2.x task for XML parsing and normalization.</remarks>
    public Task<CanonicalTransaction> NormalizeAsync(RawPayloadEnvelope rawPayload, CancellationToken ct)
    {
        throw new NotImplementedException(
            "Radix normalization is not yet implemented (RX-2.x). Select a supported FCC vendor.");
    }

    /// <inheritdoc/>
    /// <remarks>Not yet implemented — awaiting RX-2.x task for AUTH_DATA XML construction and sending.</remarks>
    public Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct)
    {
        throw new NotImplementedException(
            "Radix pre-auth is not yet implemented (RX-2.x). Select a supported FCC vendor.");
    }

    /// <inheritdoc/>
    /// <remarks>Radix does not expose real-time pump status. Always returns empty list.</remarks>
    public Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<PumpStatus>>([]);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Stub — returns false. Full implementation will use CMD_CODE=55 (product/price read)
    /// on port P+1 as the liveness probe, returning true only when RESP_CODE=201.
    /// </remarks>
    public Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        _logger.LogDebug("Radix heartbeat stub called — returning false (not yet implemented)");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Stub — returns empty batch. Full implementation will use FIFO drain loop:
    /// CMD_CODE=10 (request transaction) then CMD_CODE=201 (ACK) then repeat until empty.
    /// </remarks>
    public Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct)
    {
        return Task.FromResult(new TransactionBatch([], null, false));
    }

    /// <inheritdoc/>
    /// <remarks>Not yet implemented — awaiting RX-2.x task for cancel AUTH_DATA XML construction.</remarks>
    public Task<bool> CancelPreAuthAsync(string fccCorrelationId, CancellationToken ct)
    {
        throw new NotImplementedException(
            "Radix cancel pre-auth is not yet implemented (RX-2.x). Select a supported FCC vendor.");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op — Radix ACK (CMD_CODE=201) is sent inline during the fetch loop.
    /// There is no separate acknowledgment step required by the caller.
    /// </remarks>
    public Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct)
    {
        return Task.FromResult(true);
    }

    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary>FCC vendor identifier.</summary>
    public const string Vendor = "RADIX";

    /// <summary>Adapter version.</summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>Protocol identifier.</summary>
    public const string Protocol = "HTTP_XML";

    /// <summary>Returns true if this adapter has a working implementation.</summary>
    public const bool IsImplemented = false;

    /// <summary>Hard timeout for heartbeat probe (5 seconds).</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Successful response code.</summary>
    public const int RespCodeSuccess = 201;

    /// <summary>Signature error response code — indicates misconfigured shared secret.</summary>
    public const int RespCodeSignatureError = 251;
}
