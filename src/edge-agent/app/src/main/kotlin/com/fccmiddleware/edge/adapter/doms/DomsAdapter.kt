package com.fccmiddleware.edge.adapter.doms

import com.fccmiddleware.edge.adapter.common.*

/**
 * DomsAdapter — Edge Agent adapter for the DOMS FCC protocol.
 *
 * Communicates with the FCC over station LAN using the DOMS REST API:
 *   Base URL : http://{host}:{port}/api/v1
 *   Auth     : X-API-Key header (static key from AgentFccConfig.authCredential)
 *   Heartbeat: GET /heartbeat  → 200 { "status": "UP" }
 *   Fetch    : GET /transactions?since=&cursor=&limit=
 *   Pre-auth : POST /preauth
 *   Pump     : GET /pump-status
 *
 * Full implementation follows EA-1.x tasks.
 */
class DomsAdapter(private val config: AgentFccConfig) : IFccAdapter {

    override suspend fun normalize(rawPayload: RawPayloadEnvelope): NormalizationResult {
        return NormalizationResult.Failure(
            errorCode = "UNSUPPORTED_MESSAGE_TYPE",
            message = "DOMS adapter is not yet implemented (EA-1.x). Select a supported FCC vendor.",
        )
    }

    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        throw UnsupportedOperationException("DOMS adapter is not yet implemented (EA-1.x). Select a supported FCC vendor.")
    }

    override suspend fun cancelPreAuth(command: CancelPreAuthCommand): Boolean {
        throw UnsupportedOperationException("DOMS adapter is not yet implemented (EA-1.x). Select a supported FCC vendor.")
    }

    override suspend fun getPumpStatus(): List<PumpStatus> {
        throw UnsupportedOperationException("DOMS adapter is not yet implemented (EA-1.x). Select a supported FCC vendor.")
    }

    override suspend fun heartbeat(): Boolean {
        throw UnsupportedOperationException("DOMS adapter is not yet implemented (EA-1.x). Select a supported FCC vendor.")
    }

    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        throw UnsupportedOperationException("DOMS adapter is not yet implemented (EA-1.x). Select a supported FCC vendor.")
    }

    /** No-op — DOMS uses cursor-based acknowledgment implicit in fetchTransactions. */
    override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean = true

    companion object {
        val VENDOR = FccVendor.DOMS
        const val ADAPTER_VERSION = "1.0.0"
        const val PROTOCOL = "REST"

        /** Returns true if this adapter has a working implementation. */
        const val IS_IMPLEMENTED = false
    }
}
