package com.fccmiddleware.edge.adapter.radix

import com.fccmiddleware.edge.adapter.common.*

/**
 * RadixAdapter — Edge Agent adapter for the Radix FCC protocol.
 *
 * Communicates with the FCC over station LAN using HTTP POST with XML bodies:
 *   Auth port      : P (from config authPort) — external authorization (pre-auth)
 *   Transaction port: P+1 — transaction management, products, day close, ATG, CSR
 *   Signing        : SHA-1 hash of XML body + shared secret password
 *   Heartbeat      : CMD_CODE=55 (product/price read) — no dedicated endpoint
 *   Fetch          : FIFO drain loop: CMD_CODE=10 (request) → CMD_CODE=201 (ACK) → repeat
 *   Pre-auth       : <AUTH_DATA> XML to auth port P
 *   Pump status    : Not supported by Radix protocol
 *
 * Full implementation follows RX-1.x tasks.
 */
class RadixAdapter(private val config: AgentFccConfig) : IFccAdapter {

    override suspend fun normalize(rawPayload: RawPayloadEnvelope): NormalizationResult {
        return NormalizationResult.Failure(
            errorCode = "UNSUPPORTED_MESSAGE_TYPE",
            message = "Radix adapter is not yet implemented (RX-1.x). Select a supported FCC vendor.",
        )
    }

    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        return PreAuthResult(
            status = PreAuthResultStatus.ERROR,
            message = "Radix adapter is not yet implemented (RX-1.x). Select a supported FCC vendor.",
        )
    }

    /** Radix does not expose real-time pump status. Always returns empty list. */
    override suspend fun getPumpStatus(): List<PumpStatus> = emptyList()

    override suspend fun heartbeat(): Boolean = false

    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        return TransactionBatch(
            transactions = emptyList(),
            hasMore = false,
        )
    }

    /** No-op — Radix ACK (CMD_CODE=201) is sent inline during the fetch loop. */
    override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean = true

    companion object {
        val VENDOR = FccVendor.RADIX
        const val ADAPTER_VERSION = "1.0.0"
        const val PROTOCOL = "HTTP_XML"

        /** Returns true if this adapter has a working implementation. */
        const val IS_IMPLEMENTED = false
    }
}
