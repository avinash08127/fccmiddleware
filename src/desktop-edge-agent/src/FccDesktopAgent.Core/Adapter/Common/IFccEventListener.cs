namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Callback interface for unsolicited FCC events pushed over persistent connections.
///
/// Used by DOMS TCP/JPL adapter to notify the runtime controller of real-time events
/// without polling. Implementations must be thread-safe — callbacks may arrive
/// from the TCP read loop at any time.
/// </summary>
public interface IFccEventListener
{
    /// <summary>
    /// A pump's operational state has changed (e.g., IDLE -> CALLING -> DISPENSING).
    /// </summary>
    /// <param name="pumpNumber">Canonical pump number (after offset adjustment).</param>
    /// <param name="newState">The new canonical pump state.</param>
    /// <param name="fccStatusCode">Raw vendor-specific status code for diagnostics.</param>
    void OnPumpStatusChanged(int pumpNumber, PumpState newState, string? fccStatusCode);

    /// <summary>
    /// The FCC has signalled that one or more new transactions are available for retrieval.
    /// The controller should trigger an immediate FetchTransactionsAsync call.
    /// </summary>
    void OnTransactionAvailable(TransactionNotification notification);

    /// <summary>
    /// A fuelling-in-progress update with live volume/amount data (transient, not stored).
    /// </summary>
    void OnFuellingUpdate(int pumpNumber, long volumeMicrolitres, long amountMinorUnits);

    /// <summary>
    /// The persistent connection to the FCC has been lost unexpectedly.
    /// The controller should mark FCC as unreachable and trigger reconnect.
    /// </summary>
    void OnConnectionLost(string reason);
}

/// <summary>
/// Notification that one or more transactions are available in the FCC buffer.
/// </summary>
/// <param name="FpId">Fuelling point ID (vendor-specific pump identifier).</param>
/// <param name="TransactionBufferIndex">Index in the FCC's supervised transaction buffer.</param>
/// <param name="Timestamp">UTC timestamp when the notification was received.</param>
public sealed record TransactionNotification(
    int FpId,
    int? TransactionBufferIndex,
    DateTimeOffset Timestamp);
