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
    /// Declares the adapter's pump status capability level.
    /// Callers can check this before relying on pump status data.
    /// </summary>
    PumpStatusCapability PumpStatusCapability { get; }

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

    /// <summary>
    /// Attempt to cancel/deauthorize a previously authorized pre-auth at the FCC.
    /// Best-effort: returns true if successfully cancelled, false if not found or already terminal.
    /// Never throws on transport failure — returns false instead.
    /// </summary>
    Task<bool> CancelPreAuthAsync(string fccCorrelationId, CancellationToken ct);

    /// <summary>
    /// Acknowledge transactions so the FCC can remove them from its buffer.
    /// Vendor-specific: DOMS uses cursor-based acknowledgment (no-op here),
    /// Radix uses explicit CMD_CODE=201 ACK (no-op here — ACK is sent during fetch loop).
    /// </summary>
    /// <returns>true if acknowledgment succeeded or was not needed.</returns>
    Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct);
}
