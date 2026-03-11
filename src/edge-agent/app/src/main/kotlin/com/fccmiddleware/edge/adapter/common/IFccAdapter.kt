package com.fccmiddleware.edge.adapter.common

/**
 * Edge Kotlin FCC adapter interface.
 *
 * Contract defined in docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md §5.1.
 * All functions are suspend (coroutine-compatible).
 *
 * normalize and fetchTransactions produce canonical output — adapters own normalization.
 * sendPreAuth is edge-only; the result is returned to the caller synchronously while
 * any cloud forwarding happens asynchronously outside this interface.
 * heartbeat is a liveness probe only — true does not imply transaction or pre-auth success.
 */
interface IFccAdapter {

    /**
     * Parse one vendor payload object and produce a valid canonical transaction.
     *
     * Must reject payloads containing multiple transactions (UNSUPPORTED_MESSAGE_TYPE);
     * multi-item payloads must be iterated by the caller or by fetchTransactions.
     * Must preserve rawPayloadJson on the canonical output.
     */
    suspend fun normalize(rawPayload: RawPayloadEnvelope): CanonicalTransaction

    /**
     * Issue a pre-authorization command to the FCC over LAN.
     *
     * Must respond based on LAN-only work. Cloud forwarding is always asynchronous
     * and never on this call path.
     */
    suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult

    /**
     * Return one latest status record per configured pump-nozzle pair reachable
     * through this FCC.
     *
     * Must use short timeouts and return stale fallback metadata when FCC is slow
     * or unreachable.
     */
    suspend fun getPumpStatus(): List<PumpStatus>

    /**
     * Connectivity liveness probe.
     *
     * true means authenticated protocol reachability succeeded.
     * Does not imply transaction fetch or pre-auth success.
     */
    suspend fun heartbeat(): Boolean

    /**
     * Fetch transactions from the FCC over LAN.
     *
     * Same cursor progression contract and batch semantics as the cloud adapter.
     * Must be side-effect free on vendor state except for vendor-defined cursor
     * acknowledgment implicit in the request parameters.
     * Overlapping transactions across fetch calls must not be suppressed.
     */
    suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch
}
