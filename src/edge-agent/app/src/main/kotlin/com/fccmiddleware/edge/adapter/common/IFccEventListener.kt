package com.fccmiddleware.edge.adapter.common

/**
 * Callback interface for unsolicited FCC events pushed over persistent connections.
 *
 * Used by DOMS TCP/JPL adapter to notify CadenceController of real-time events
 * without polling. Implementations must be thread-safe — callbacks may arrive
 * from the TCP read loop coroutine at any time.
 *
 * All callbacks are fire-and-forget from the adapter's perspective. Implementations
 * should not block or throw — errors must be caught and logged internally.
 */
interface IFccEventListener {

    /**
     * A pump's operational state has changed (e.g., IDLE -> CALLING -> DISPENSING).
     *
     * @param pumpNumber Canonical pump number (after offset adjustment).
     * @param newState The new canonical pump state.
     * @param fccStatusCode Raw vendor-specific status code for diagnostics.
     */
    fun onPumpStatusChanged(pumpNumber: Int, newState: PumpState, fccStatusCode: String?)

    /**
     * The FCC has signalled that one or more new transactions are available for retrieval.
     *
     * CadenceController should trigger an immediate fetchTransactions() call.
     *
     * @param notification Details about the available transaction(s).
     */
    fun onTransactionAvailable(notification: TransactionNotification)

    /**
     * A fuelling-in-progress update with live volume/amount data.
     *
     * Used for real-time display updates. Not stored — purely transient.
     *
     * @param pumpNumber Canonical pump number.
     * @param volumeMicrolitres Current dispensed volume in microlitres.
     * @param amountMinorUnits Current dispensed amount in minor currency units.
     */
    fun onFuellingUpdate(pumpNumber: Int, volumeMicrolitres: Long, amountMinorUnits: Long)

    /**
     * The persistent connection to the FCC has been lost unexpectedly.
     *
     * CadenceController should mark FCC as unreachable and trigger reconnect logic.
     *
     * @param reason Human-readable description of the disconnection cause.
     */
    fun onConnectionLost(reason: String)
}

/**
 * Notification that one or more transactions are available in the FCC buffer.
 *
 * @param fpId Fuelling point ID (vendor-specific pump identifier).
 * @param transactionBufferIndex Index in the FCC's supervised transaction buffer.
 * @param timestamp ISO 8601 UTC timestamp when the notification was received.
 */
data class TransactionNotification(
    val fpId: Int,
    val transactionBufferIndex: Int? = null,
    val timestamp: String,
)
