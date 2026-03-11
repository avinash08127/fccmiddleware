package com.fccmiddleware.edge.sync

import kotlinx.serialization.Serializable

// ---------------------------------------------------------------------------
// Upload request — POST /api/v1/transactions/upload
// Matches CanonicalTransaction schema v1. Records must be in ascending completedAt order.
// ---------------------------------------------------------------------------

@Serializable
data class CloudUploadRequest(
    /** Batch of canonical transactions in ascending completedAt order. Max 500. */
    val transactions: List<CloudTransactionDto>,
)

/**
 * Edge Agent → Cloud upload DTO.
 *
 * Mirrors the cloud-side CanonicalTransaction schema v1 field-for-field.
 * Money fields: Long minor units. Timestamps: ISO 8601 UTC. UUIDs: String.
 */
@Serializable
data class CloudTransactionDto(
    /** Middleware-generated UUID (local buffer id). */
    val id: String,

    /** Opaque FCC transaction ID. Dedup key together with siteCode. */
    val fccTransactionId: String,

    val siteCode: String,

    val pumpNumber: Int,

    val nozzleNumber: Int,

    val productCode: String,

    /** Dispensed volume in microlitres. */
    val volumeMicrolitres: Long,

    /** Total amount in minor currency units. */
    val amountMinorUnits: Long,

    /** Price per litre in minor currency units. */
    val unitPriceMinorPerLitre: Long,

    /** ISO 4217 currency code. */
    val currencyCode: String,

    /** ISO 8601 UTC dispense start. */
    val startedAt: String,

    /** ISO 8601 UTC dispense completion. */
    val completedAt: String,

    /** FCC vendor name (enum string). */
    val fccVendor: String,

    /** Legal entity owning the site. Denormalised for row-level scoping. */
    val legalEntityId: String,

    /** Transaction lifecycle status (PENDING until cloud confirms). */
    val status: String,

    /** Which ingestion path delivered this transaction (EDGE_UPLOAD). */
    val ingestionSource: String,

    /** ISO 8601 UTC when Edge Agent first persisted this transaction. */
    val ingestedAt: String,

    /** ISO 8601 UTC of last status change. */
    val updatedAt: String,

    val schemaVersion: Int,

    /** Always false for Edge-uploaded records; cloud performs dedup check. */
    val isDuplicate: Boolean = false,

    /** Trace correlation ID. */
    val correlationId: String,

    val fiscalReceiptNumber: String? = null,
    val attendantId: String? = null,

    /**
     * Raw FCC payload JSON. Sent to cloud for archival when present.
     * Cloud will convert to rawPayloadRef (S3 URI) and drop the inline payload.
     */
    val rawPayloadJson: String? = null,
)

// ---------------------------------------------------------------------------
// Upload response
// ---------------------------------------------------------------------------

@Serializable
data class CloudUploadResponse(
    val results: List<CloudUploadRecordResult>,
    val acceptedCount: Int,
    val duplicateCount: Int,
    val rejectedCount: Int,
)

@Serializable
data class CloudUploadRecordResult(
    val fccTransactionId: String,
    val siteCode: String,

    /**
     * Per-record outcome:
     *   ACCEPTED  — persisted; id is the cloud UUID.
     *   DUPLICATE — already exists; id is the existing cloud UUID.
     *   REJECTED  — validation or adapter error; error contains details.
     */
    val outcome: String,

    /** Cloud UUID on ACCEPTED/DUPLICATE; null on REJECTED. */
    val id: String? = null,

    /** Populated only on REJECTED outcome. */
    val error: CloudErrorResponse? = null,
)

@Serializable
data class CloudErrorResponse(
    val errorCode: String,
    val message: String,
)

/** Typed outcome values matching the cloud API enum. */
enum class UploadOutcome {
    ACCEPTED, DUPLICATE, REJECTED
}

// ---------------------------------------------------------------------------
// Synced-status poll — GET /api/v1/transactions/synced-status
// ---------------------------------------------------------------------------

/**
 * Response from `GET /api/v1/transactions/synced-status?ids=...`.
 *
 * Returns the cloud-side status for each requested transaction ID.
 */
@Serializable
data class SyncedStatusResponse(
    val statuses: List<TransactionStatusEntry>,
)

/**
 * Per-transaction status entry within a [SyncedStatusResponse].
 *
 * [id] is the FCC transaction ID (dedup key).
 * [status] is one of: PENDING, SYNCED, SYNCED_TO_ODOO, STALE_PENDING, DUPLICATE, ARCHIVED, NOT_FOUND.
 */
@Serializable
data class TransactionStatusEntry(
    val id: String,
    val status: String,
)

// ---------------------------------------------------------------------------
// Config poll — GET /api/v1/agent/config
// ---------------------------------------------------------------------------

/**
 * Result of polling `GET /api/v1/agent/config` with `If-None-Match`.
 */
sealed class CloudConfigPollResult {
    /** HTTP 200 — new config snapshot available. */
    data class Success(
        /** Raw JSON body for persistence to Room. */
        val rawJson: String,
        /** ETag header value (configVersion as string). */
        val etag: String?,
    ) : CloudConfigPollResult()

    /** HTTP 304 — config unchanged since last poll. */
    data object NotModified : CloudConfigPollResult()

    /** HTTP 401 — token expired; caller should refresh and retry. */
    data object Unauthorized : CloudConfigPollResult()

    /** HTTP 403 — forbidden (possibly decommissioned). */
    data class Forbidden(val errorCode: String?) : CloudConfigPollResult()

    /** Network or unexpected HTTP error. */
    data class TransportError(val message: String) : CloudConfigPollResult()
}
