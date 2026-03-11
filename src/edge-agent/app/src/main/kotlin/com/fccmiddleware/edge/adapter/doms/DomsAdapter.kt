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

    override suspend fun normalize(rawPayload: RawPayloadEnvelope): CanonicalTransaction {
        TODO("Implement DOMS normalize — EA-1.x")
    }

    override suspend fun sendPreAuth(command: PreAuthCommand): PreAuthResult {
        TODO("Implement DOMS pre-auth relay — EA-1.x")
    }

    override suspend fun getPumpStatus(): List<PumpStatus> {
        TODO("Implement DOMS pump status fetch — EA-1.x")
    }

    override suspend fun heartbeat(): Boolean {
        TODO("Implement DOMS heartbeat probe — EA-1.x")
    }

    override suspend fun fetchTransactions(cursor: FetchCursor): TransactionBatch {
        TODO("Implement DOMS transaction fetch — EA-1.x")
    }

    companion object {
        val VENDOR = FccVendor.DOMS
        const val ADAPTER_VERSION = "1.0.0"
        const val PROTOCOL = "REST"
    }
}
