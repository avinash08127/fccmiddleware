package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.security.Sensitive
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

    /** M-15: HTTP 429 — rate limited by cloud. */
    data class RateLimited(val retryAfterSeconds: Long?) : CloudConfigPollResult()

    /** Network or unexpected HTTP error. */
    data class TransportError(val message: String) : CloudConfigPollResult()
}

// ---------------------------------------------------------------------------
// Telemetry — POST /api/v1/agent/telemetry
// ---------------------------------------------------------------------------

/**
 * Full telemetry payload sent to cloud. Matches telemetry-payload.schema.json v1.0.
 * One payload per reporting interval per device. Fire-and-forget.
 */
@Serializable
data class TelemetryPayload(
    val schemaVersion: String = "1.0",
    val deviceId: String,
    val siteCode: String,
    val legalEntityId: String,
    val reportedAtUtc: String,
    val sequenceNumber: Long,
    val connectivityState: String,
    val device: DeviceStatusDto,
    val fccHealth: FccHealthStatusDto,
    val buffer: BufferStatusDto,
    val sync: SyncStatusDto,
    val errorCounts: ErrorCountsDto,
)

@Serializable
data class DeviceStatusDto(
    val batteryPercent: Int,
    val isCharging: Boolean,
    val storageFreeMb: Int,
    val storageTotalMb: Int,
    val memoryFreeMb: Int,
    val memoryTotalMb: Int,
    val appVersion: String,
    val appUptimeSeconds: Int,
    val osVersion: String,
    val deviceModel: String,
)

@Serializable
data class FccHealthStatusDto(
    val isReachable: Boolean,
    val lastHeartbeatAtUtc: String?,
    val heartbeatAgeSeconds: Int?,
    val fccVendor: String,
    val fccHost: String,
    val fccPort: Int,
    val consecutiveHeartbeatFailures: Int,
)

@Serializable
data class BufferStatusDto(
    val totalRecords: Int,
    val pendingUploadCount: Int,
    val syncedCount: Int,
    val syncedToOdooCount: Int,
    val failedCount: Int,
    val oldestPendingAtUtc: String?,
    val bufferSizeMb: Int,
)

@Serializable
data class SyncStatusDto(
    val lastSyncAttemptUtc: String?,
    val lastSuccessfulSyncUtc: String?,
    val syncLagSeconds: Int?,
    val lastStatusPollUtc: String?,
    val lastConfigPullUtc: String?,
    val configVersion: String?,
    val uploadBatchSize: Int,
)

@Serializable
data class ErrorCountsDto(
    val fccConnectionErrors: Int,
    val cloudUploadErrors: Int,
    val cloudAuthErrors: Int,
    val localApiErrors: Int,
    val bufferWriteErrors: Int,
    val adapterNormalizationErrors: Int,
    val preAuthErrors: Int,
)

// ---------------------------------------------------------------------------
// Device Registration — POST /api/v1/agent/register
// ---------------------------------------------------------------------------

/**
 * Request body for POST /api/v1/agent/register.
 * Matches DeviceRegistrationRequest schema from device-registration.schema.json.
 */
@Serializable
data class DeviceRegistrationRequest(
    @Sensitive val provisioningToken: String,
    val siteCode: String,
    val deviceSerialNumber: String,
    val deviceModel: String,
    val osVersion: String,
    val agentVersion: String,
    val replacePreviousAgent: Boolean = false,
)

/**
 * Response from POST /api/v1/agent/register (HTTP 201).
 * Matches DeviceRegistrationResponse schema + refreshToken from security spec §5.1.
 */
@Serializable
data class DeviceRegistrationResponse(
    val deviceId: String,
    @Sensitive val deviceToken: String,
    @Sensitive val refreshToken: String,
    val tokenExpiresAt: String,
    val siteCode: String,
    val legalEntityId: String,
    val siteConfig: kotlinx.serialization.json.JsonObject? = null,
    val registeredAt: String,
)

/** Result of calling POST /api/v1/agent/register. */
sealed class CloudRegistrationResult {
    /** HTTP 201 — registration successful. */
    data class Success(val response: DeviceRegistrationResponse) : CloudRegistrationResult()

    /** HTTP 400/409 — bad request or conflict (token used, site mismatch, etc.). */
    data class Rejected(val errorCode: String, val message: String) : CloudRegistrationResult()

    /** Network or unexpected HTTP error. */
    data class TransportError(val message: String) : CloudRegistrationResult()
}

// ---------------------------------------------------------------------------
// Token Refresh — POST /api/v1/agent/token/refresh
// ---------------------------------------------------------------------------

@Serializable
data class TokenRefreshRequest(
    @Sensitive val refreshToken: String,
)

@Serializable
data class TokenRefreshResponse(
    @Sensitive val deviceToken: String,
    @Sensitive val refreshToken: String,
    val tokenExpiresAt: String,
)

/** Result of calling POST /api/v1/agent/token/refresh. */
sealed class CloudTokenRefreshResult {
    /** HTTP 200 — new tokens issued. */
    data class Success(val response: TokenRefreshResponse) : CloudTokenRefreshResult()

    /** HTTP 401 — refresh token expired or invalid. Device must re-provision. */
    data object Unauthorized : CloudTokenRefreshResult()

    /** HTTP 403 — device decommissioned. */
    data class Forbidden(val errorCode: String?) : CloudTokenRefreshResult()

    /** Network or unexpected HTTP error. */
    data class TransportError(val message: String) : CloudTokenRefreshResult()
}

// ---------------------------------------------------------------------------
// Pre-Auth Forward — POST /api/v1/preauth
// ---------------------------------------------------------------------------

/**
 * Request body for POST /api/v1/preauth.
 * Matches PreAuthForwardRequest schema from cloud-api.yaml.
 *
 * The Edge Agent forwards pre-auth records to cloud for tracking.
 * Dedup key: (odooOrderId, siteCode). Re-posting with the same key and
 * an updated status triggers a status transition on the cloud record.
 */
@Serializable
data class PreAuthForwardRequest(
    val siteCode: String,
    val odooOrderId: String,
    val pumpNumber: Int,
    val nozzleNumber: Int,
    val productCode: String,
    /** Minor currency units. */
    val requestedAmount: Long,
    /** Price per litre in minor units at authorization time. */
    val unitPrice: Long,
    /** ISO 4217 currency code. */
    val currency: String,
    /** PreAuth lifecycle status. */
    val status: String,
    /** ISO 8601 UTC. */
    val requestedAt: String,
    /** ISO 8601 UTC. */
    val expiresAt: String,
    val fccCorrelationId: String? = null,
    val fccAuthorizationCode: String? = null,
    val vehicleNumber: String? = null,
    val customerName: String? = null,
    val customerTaxId: String? = null,
    val customerBusinessName: String? = null,
    val attendantId: String? = null,
)

/**
 * Response from POST /api/v1/preauth (HTTP 200/201).
 * Matches PreAuthForwardResponse schema from cloud-api.yaml.
 */
@Serializable
data class PreAuthForwardResponse(
    val id: String,
    val status: String,
    val siteCode: String,
    val odooOrderId: String,
    val createdAt: String? = null,
    val updatedAt: String? = null,
)

/** Result of calling POST /api/v1/preauth. */
sealed class CloudPreAuthForwardResult {
    /** HTTP 200/201 — pre-auth record created or updated on cloud. */
    data class Success(val response: PreAuthForwardResponse) : CloudPreAuthForwardResult()

    /** HTTP 401 — token expired; caller should refresh and retry. */
    data object Unauthorized : CloudPreAuthForwardResult()

    /** HTTP 403 — forbidden (possibly decommissioned). */
    data class Forbidden(val errorCode: String?) : CloudPreAuthForwardResult()

    /** M-15: HTTP 429 — rate limited by cloud. */
    data class RateLimited(val retryAfterSeconds: Long?) : CloudPreAuthForwardResult()

    /** HTTP 409 — invalid state transition on cloud. Treated as success (record exists). */
    data class Conflict(val errorCode: String?, val message: String?) : CloudPreAuthForwardResult()

    /** Network or non-2xx/401/403/409 failure. */
    data class TransportError(val message: String) : CloudPreAuthForwardResult()
}

/** Result of submitting telemetry to cloud. */
sealed class CloudTelemetryResult {
    /** HTTP 204 — telemetry accepted. */
    data object Success : CloudTelemetryResult()

    /** HTTP 401 — token expired. */
    data object Unauthorized : CloudTelemetryResult()

    /** HTTP 403 — forbidden (possibly decommissioned). */
    data class Forbidden(val errorCode: String?) : CloudTelemetryResult()

    /** M-15: HTTP 429 — rate limited by cloud. */
    data class RateLimited(val retryAfterSeconds: Long?) : CloudTelemetryResult()

    /** Network or non-2xx/401/403 failure. */
    data class TransportError(val message: String) : CloudTelemetryResult()
}
