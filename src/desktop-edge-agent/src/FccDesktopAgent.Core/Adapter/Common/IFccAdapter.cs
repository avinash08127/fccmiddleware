namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Abstraction over a specific FCC hardware protocol (e.g. DOMS).
/// All methods accept CancellationToken. Never throws on FCC unreachability —
/// returns failure results or false instead.
/// </summary>
public interface IFccAdapter
{
    /// <summary>
    /// Normalize a raw FCC payload into a canonical transaction.
    /// </summary>
    Task<CanonicalTransaction> NormalizeAsync(RawPayloadEnvelope rawPayload, CancellationToken ct);

    /// <summary>
    /// Send a pre-authorization command to the FCC and return the result.
    /// p95 local overhead target: &lt;= 50 ms before FCC call time.
    /// </summary>
    Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct);

    /// <summary>
    /// Fetch the current pump status snapshot from the FCC.
    /// Returns an empty list if the FCC is unreachable.
    /// </summary>
    Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct);

    /// <summary>
    /// Check FCC reachability. Returns true if the FCC responded within timeout.
    /// </summary>
    Task<bool> HeartbeatAsync(CancellationToken ct);

    /// <summary>
    /// Fetch new transactions from the FCC starting at the given cursor position.
    /// </summary>
    Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct);
}
