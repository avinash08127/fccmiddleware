package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.buffer.dao.LocalApiTransaction
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import kotlinx.serialization.Serializable

/**
 * Standard error envelope — consistent with the Cloud API and edge-agent-local-api.yaml ErrorResponse.
 */
@Serializable
data class ErrorResponse(
    val errorCode: String,
    val message: String,
    val traceId: String,
    val timestamp: String,
)

/**
 * Agent operational status snapshot — matches AgentStatusResponse in edge-agent-local-api.yaml.
 */
@Serializable
data class AgentStatusResponse(
    val deviceId: String,
    val siteCode: String,
    val connectivityState: String,
    val fccReachable: Boolean,
    val fccHeartbeatAgeSeconds: Int? = null,
    val bufferDepth: Int,
    val syncLagSeconds: Int? = null,
    val lastSuccessfulSyncUtc: String? = null,
    val configVersion: Int? = null,
    val agentVersion: String,
    val uptimeSeconds: Int,
    val reportedAtUtc: String,
)

/**
 * A buffered transaction as exposed through the local REST API.
 * Excludes internal sync metadata that Odoo POS doesn't need.
 * Matches LocalTransaction in edge-agent-local-api.yaml.
 */
@Serializable
data class LocalTransaction(
    val id: String,
    val fccTransactionId: String,
    val siteCode: String,
    val pumpNumber: Int,
    val nozzleNumber: Int,
    val productCode: String,
    /** Microlitres */
    val volumeMicrolitres: Long,
    /** Minor currency units (cents). NEVER floating point. */
    val amountMinorUnits: Long,
    /** Minor units per litre. NEVER floating point. */
    val unitPriceMinorPerLitre: Long,
    val currencyCode: String,
    /** ISO 8601 UTC */
    val startedAt: String,
    /** ISO 8601 UTC */
    val completedAt: String,
    val fiscalReceiptNumber: String? = null,
    val fccVendor: String,
    val attendantId: String? = null,
    /** SyncStatus: PENDING | UPLOADED */
    val syncStatus: String,
    val correlationId: String,
) {
    companion object {
        fun from(entity: BufferedTransaction) = LocalTransaction(
            id = entity.id,
            fccTransactionId = entity.fccTransactionId,
            siteCode = entity.siteCode,
            pumpNumber = entity.pumpNumber,
            nozzleNumber = entity.nozzleNumber,
            productCode = entity.productCode,
            volumeMicrolitres = entity.volumeMicrolitres,
            amountMinorUnits = entity.amountMinorUnits,
            unitPriceMinorPerLitre = entity.unitPriceMinorPerLitre,
            currencyCode = entity.currencyCode,
            startedAt = entity.startedAt,
            completedAt = entity.completedAt,
            fiscalReceiptNumber = entity.fiscalReceiptNumber,
            fccVendor = entity.fccVendor,
            attendantId = entity.attendantId,
            syncStatus = entity.syncStatus,
            correlationId = entity.correlationId,
        )

        /** AP-035: Factory from lightweight projection (excludes rawPayloadJson). */
        fun from(proj: LocalApiTransaction) = LocalTransaction(
            id = proj.id,
            fccTransactionId = proj.fccTransactionId,
            siteCode = proj.siteCode,
            pumpNumber = proj.pumpNumber,
            nozzleNumber = proj.nozzleNumber,
            productCode = proj.productCode,
            volumeMicrolitres = proj.volumeMicrolitres,
            amountMinorUnits = proj.amountMinorUnits,
            unitPriceMinorPerLitre = proj.unitPriceMinorPerLitre,
            currencyCode = proj.currencyCode,
            startedAt = proj.startedAt,
            completedAt = proj.completedAt,
            fiscalReceiptNumber = proj.fiscalReceiptNumber,
            fccVendor = proj.fccVendor,
            attendantId = proj.attendantId,
            syncStatus = proj.syncStatus,
            correlationId = proj.correlationId,
        )
    }
}

/**
 * Paginated list response for GET /api/v1/transactions.
 */
@Serializable
data class TransactionListResponse(
    val transactions: List<LocalTransaction>,
    /** Total records matching the filter (excludes SYNCED_TO_ODOO). */
    val total: Int,
    val limit: Int,
    val offset: Int,
)

/**
 * Batch acknowledge request for POST /api/v1/transactions/acknowledge.
 * Odoo POS marks a list of transactions as locally consumed.
 */
@Serializable
data class BatchAcknowledgeRequest(
    val transactionIds: List<String>,
)

/**
 * Response for POST /api/v1/transactions/acknowledge.
 */
@Serializable
data class BatchAcknowledgeResponse(
    val acknowledged: Int,
)

/**
 * Wrapper for GET /api/v1/pump-status response with stale metadata.
 */
@Serializable
data class PumpStatusResponse(
    val pumps: List<com.fccmiddleware.edge.adapter.common.PumpStatus>,
    /** Adapter-declared pump status capability level. */
    val capability: String? = null,
    /** Human-readable reason when capability is NOT_SUPPORTED or NOT_APPLICABLE. */
    val reason: String? = null,
    /** true when FCC was unreachable and data is from the last-known cache. */
    val stale: Boolean = false,
    /** Age of the cached data in seconds; null when live. */
    val dataAgeSeconds: Int? = null,
    /** ISO 8601 UTC timestamp when data was fetched; null if not yet available. */
    val fetchedAtUtc: String? = null,
)

/**
 * Cancel pre-auth request body for POST /api/v1/preauth/cancel.
 */
@Serializable
data class CancelPreAuthRequest(
    val odooOrderId: String,
    val siteCode: String,
)

/**
 * Response for POST /api/v1/preauth/cancel.
 */
@Serializable
data class CancelPreAuthResponse(
    val success: Boolean,
    val message: String? = null,
)

/**
 * Optional request body for POST /api/v1/transactions/pull.
 * All fields are optional; omitting the body is equivalent to passing an empty object.
 */
@Serializable
data class ManualPullRequest(
    /**
     * Informational pump number. Logged for diagnostics but does NOT restrict the fetch;
     * the adapter returns all transactions since the last cursor. Pump-specific filtering
     * is applied at the query layer, not during ingestion.
     */
    val pumpNumber: Int? = null,
)

/**
 * Response for POST /api/v1/transactions/pull.
 */
@Serializable
data class ManualPullResponse(
    /** Transactions newly inserted into the local buffer during this pull. */
    val newCount: Int,
    /** Transactions skipped because they were already buffered (dedup). */
    val skippedCount: Int,
    /** Number of FCC fetch iterations performed. */
    val fetchCycles: Int,
    /** True if the FCC cursor was advanced at least once. */
    val cursorAdvanced: Boolean,
    /** UTC timestamp when the pull was triggered. */
    val triggeredAtUtc: String,
    /**
     * AF-006: Number of newly buffered transactions matching the requested pumpNumber.
     * Null when no pumpNumber filter was provided in the request.
     */
    val pumpMatchCount: Int? = null,
)
