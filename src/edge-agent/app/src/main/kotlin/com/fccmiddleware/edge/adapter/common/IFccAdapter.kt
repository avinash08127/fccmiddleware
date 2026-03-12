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
     * ## Error contract
     * Returns [NormalizationResult.Success] on valid input, or [NormalizationResult.Failure]
     * with a machine-readable error code on rejection. Must never throw — all errors must
     * be returned via the sealed result.
     *
     * ## Error codes
     * - `UNSUPPORTED_MESSAGE_TYPE` — payload contains multiple transactions or an
     *   unrecognised message type; multi-item payloads must be iterated by the caller
     *   or by [fetchTransactions].
     * - `INVALID_PAYLOAD` — payload cannot be parsed (malformed JSON/XML, encoding error).
     * - `MISSING_REQUIRED_FIELD` — a field required by the canonical schema is absent
     *   in the vendor payload and has no safe default.
     * - `MALFORMED_FIELD` — a field is present but its value cannot be converted to
     *   the canonical type (e.g. non-numeric amount, invalid date format).
     *
     * ## Timeout contract
     * Implementations must complete within 500 ms under normal conditions. Callers
     * should enforce an external [withTimeout] as a safety net; the adapter must not
     * perform network I/O during normalization.
     *
     * ## Nullable field handling
     * Optional fields absent in the vendor payload must be set to `null` on the
     * canonical output — never to empty strings or placeholder defaults.
     * Must preserve [CanonicalTransaction.rawPayloadJson] on the output.
     */
    suspend fun normalize(rawPayload: RawPayloadEnvelope): NormalizationResult

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

    /**
     * Acknowledge transactions so the FCC can remove them from its buffer.
     *
     * Vendor-specific: DOMS uses cursor-based acknowledgment (no-op here),
     * Radix uses explicit CMD_CODE=201 ACK (no-op here — ACK is sent during fetch loop).
     *
     * @return true if acknowledgment succeeded or was not needed.
     */
    suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean
}
