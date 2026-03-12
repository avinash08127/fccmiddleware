package com.fccmiddleware.edge.adapter.petronite

import com.fccmiddleware.edge.adapter.common.*

/**
 * PetroniteAdapter — Edge Agent adapter for the Petronite FCC protocol.
 *
 * Communicates with the FCC over station LAN using REST/JSON with OAuth2 Client Credentials:
 *   Auth     : OAuth2 Client Credentials (POST /oauth/token with Basic auth)
 *   Heartbeat: GET /nozzles/assigned as liveness probe
 *   Fetch    : Push-only via webhook — fetchTransactions returns empty (no pull)
 *   Pre-auth : Two-step: POST /direct-authorize-requests/create + /authorize
 *   Cancel   : POST /direct-authorize-requests/{id}/cancel
 *   Pump     : Synthesized from nozzle assignments + pending orders
 *
 * Full implementation follows PN-1.x / PN-2.x tasks.
 */
class PetroniteAdapter(private val config: AgentFccConfig) : IFccAdapter {

    override suspend fun normalize(rawPayload: RawPayloadEnvelope): NormalizationResult {
        return NormalizationResult.Failure(
            errorCode = "UNSUPPORTED_MESSAGE_TYPE",
            message = "Petronite adapter is not yet implemented (PN-1.x). Select a supported FCC vendor.",
        )
    }

    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        return PreAuthResult(
            status = PreAuthResultStatus.ERROR,
            message = "Petronite adapter is not yet implemented (PN-1.x). Select a supported FCC vendor.",
        )
    }

    /** Petronite pump status synthesized from nozzle assignments + pending orders. */
    override suspend fun getPumpStatus(): List<PumpStatus> = emptyList()

    override suspend fun heartbeat(): Boolean = false

    /** Push-only — Petronite transactions arrive via webhook, not polling. */
    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        return TransactionBatch(
            transactions = emptyList(),
            hasMore = false,
        )
    }

    /** No-op — Petronite has no explicit transaction acknowledgment. */
    override suspend fun acknowledgeTransactions(transactionIds: List<String>): Boolean = true

    companion object {
        val VENDOR = FccVendor.PETRONITE
        const val ADAPTER_VERSION = "1.0.0"
        const val PROTOCOL = "REST_JSON"

        /** Returns true if this adapter has a working implementation. */
        const val IS_IMPLEMENTED = false
    }
}
