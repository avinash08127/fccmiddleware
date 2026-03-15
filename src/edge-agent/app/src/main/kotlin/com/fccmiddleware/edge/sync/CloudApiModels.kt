package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.security.Sensitive
import com.fccmiddleware.edge.security.SensitiveFieldFilter
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject

// ---------------------------------------------------------------------------
// Upload request — POST /api/v1/transactions/upload
// Matches CanonicalTransaction schema v1. Records must be in ascending completedAt order.
// ---------------------------------------------------------------------------

@Serializable
data class CloudUploadRequest(
    /** Batch of canonical transactions in ascending completedAt order. Max 500. */
    val transactions: List<CloudTransactionDto>,
    /** Current site leader epoch for cloud stale-writer fencing. */
    val leaderEpoch: Long? = null,
    /** Batch-level idempotency key. Cloud caches results keyed by this ID. */
    val uploadBatchId: String? = null,
)

/**
 * Edge Agent → Cloud upload DTO.
 *
 * Aligned 1:1 with the cloud-side [UploadTransactionRecord] contract.
 * Money fields: Long minor units. Timestamps: ISO 8601 UTC. UUIDs: String.
 */
@Serializable
data class CloudTransactionDto(
    /** Opaque FCC transaction ID. Dedup key together with siteCode. */
    val fccTransactionId: String,

    val siteCode: String,

    /** FCC vendor name (enum string). */
    val fccVendor: String,

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

    /** FCC-side pre-auth correlation ID echoed on the final dispense when available. */
    val fccCorrelationId: String? = null,

    /** Odoo order ID echoed by the FCC when available. */
    val odooOrderId: String? = null,

    val fiscalReceiptNumber: String? = null,
    val attendantId: String? = null,
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

    /**
     * Per-record outcome:
     *   ACCEPTED  — persisted; transactionId is the cloud UUID.
     *   DUPLICATE — already exists; originalTransactionId is the existing cloud UUID.
     *   REJECTED  — validation or adapter error; errorCode/errorMessage contain details.
     */
    val outcome: String,

    /** Cloud UUID on ACCEPTED; null on DUPLICATE/REJECTED. */
    val transactionId: String? = null,

    /** Existing cloud UUID on DUPLICATE; null on ACCEPTED/REJECTED. */
    val originalTransactionId: String? = null,

    /** Populated only on REJECTED outcome. */
    val errorCode: String? = null,

    /** Populated only on REJECTED outcome. */
    val errorMessage: String? = null,
)

@Serializable
data class CloudErrorResponse(
    val errorCode: String,
    val message: String,
    /** NET-015: Backend `Retryable` hint — when false the error is permanent (no point retrying). */
    val retryable: Boolean? = null,
)

/** Typed outcome values matching the cloud API enum. */
enum class UploadOutcome {
    ACCEPTED, DUPLICATE, REJECTED
}

// ---------------------------------------------------------------------------
// Synced-status poll — GET /api/v1/transactions/synced-status
// ---------------------------------------------------------------------------

/**
 * Response from `GET /api/v1/transactions/synced-status?since=...`.
 *
 * Returns FCC transaction IDs that reached SYNCED_TO_ODOO since the requested timestamp.
 */
@Serializable
data class SyncedStatusResponse(
    val fccTransactionIds: List<String>,
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
        /** Cloud's peer directory version from X-Peer-Directory-Version header. */
        val peerDirectoryVersion: Long? = null,
    ) : CloudConfigPollResult()

    /** HTTP 304 — config unchanged since last poll. Peer directory version still captured. */
    data class NotModified(
        /** Cloud's peer directory version from X-Peer-Directory-Version header. */
        val peerDirectoryVersion: Long? = null,
    ) : CloudConfigPollResult()

    /** HTTP 401 — token expired; caller should refresh and retry. */
    data object Unauthorized : CloudConfigPollResult()

    /** HTTP 403 — forbidden (possibly decommissioned). */
    data class Forbidden(val errorCode: String?) : CloudConfigPollResult()

    /** HTTP 404 — config or device not found (permanent error, do not retry). */
    data class NotFound(val errorCode: String?, val message: String?) : CloudConfigPollResult()

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
    val sequenceNumber: Int,
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
    /** AT-037: Count of records permanently failed after max upload retries (DEAD_LETTER status). */
    val deadLetterCount: Int = 0,
    /** AT-037: Count of records archived after successful sync lifecycle completion. */
    val archivedCount: Int = 0,
    val oldestPendingAtUtc: String?,
    val bufferSizeMb: Int,
    val fiscalPendingCount: Int = 0,
    val fiscalDeadLetterCount: Int = 0,
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
data class PeerApiRegistrationMetadata(
    val baseUrl: String? = null,
    val advertisedHost: String? = null,
    val port: Int? = null,
    val tlsEnabled: Boolean = false,
)

@Serializable
data class DeviceRegistrationRequest(
    @Sensitive val provisioningToken: String,
    val siteCode: String,
    val deviceSerialNumber: String,
    val deviceModel: String,
    val osVersion: String,
    val agentVersion: String,
    val deviceClass: String = "ANDROID",
    val roleCapability: String? = null,
    val siteHaPriority: Int? = null,
    val capabilities: List<String> = emptyList(),
    val peerApi: PeerApiRegistrationMetadata? = null,
    val replacePreviousAgent: Boolean = false,
) {
    override fun toString(): String = SensitiveFieldFilter.redactToString(this)
}

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
) {
    override fun toString(): String = SensitiveFieldFilter.redactToString(this)
}

/** Result of calling POST /api/v1/agent/register. */
sealed class CloudRegistrationResult {
    /** HTTP 201 — registration successful. */
    data class Success(val response: DeviceRegistrationResponse) : CloudRegistrationResult()

    /** HTTP 400/409 — bad request or conflict (token used, site mismatch, etc.). */
    data class Rejected(val errorCode: String, val message: String) : CloudRegistrationResult()

    /** HTTP 401 — invalid or expired provisioning token (permanent, do not retry). */
    data object Unauthorized : CloudRegistrationResult()

    /** HTTP 429 — rate limited. */
    data class RateLimited(val retryAfterSeconds: Long?) : CloudRegistrationResult()

    /** Network or unexpected HTTP error. */
    data class TransportError(val message: String) : CloudRegistrationResult()
}

// ---------------------------------------------------------------------------
// Token Refresh — POST /api/v1/agent/token/refresh
// ---------------------------------------------------------------------------

@Serializable
data class TokenRefreshRequest(
    @Sensitive val refreshToken: String,
    // FM-S03: Include the current (even expired) device JWT to bind refresh to device identity
    @Sensitive val deviceToken: String,
) {
    override fun toString(): String = SensitiveFieldFilter.redactToString(this)
}

@Serializable
data class TokenRefreshResponse(
    @Sensitive val deviceToken: String,
    @Sensitive val refreshToken: String,
    val tokenExpiresAt: String,
) {
    override fun toString(): String = SensitiveFieldFilter.redactToString(this)
}

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
// Agent Control — GET /api/v1/agent/commands, POST /api/v1/agent/commands/{id}/ack
// ---------------------------------------------------------------------------

@Serializable
enum class AgentCommandType {
    FORCE_CONFIG_PULL,
    RESET_LOCAL_STATE,
    DECOMMISSION,
    PLANNED_SWITCHOVER,
    REFRESH_CONFIG,
}

@Serializable
enum class AgentCommandStatus {
    PENDING,
    DELIVERY_HINT_SENT,
    ACKED,
    FAILED,
    EXPIRED,
    CANCELLED,
}

@Serializable
enum class AgentCommandCompletionStatus {
    ACKED,
    FAILED,
}

@Serializable
data class EdgeCommandPollResponse(
    val serverTimeUtc: String,
    val commands: List<EdgeCommandDto>,
)

@Serializable
data class EdgeCommandDto(
    val commandId: String,
    val commandType: AgentCommandType,
    val status: AgentCommandStatus,
    val reason: String,
    val payload: JsonElement? = null,
    val createdAt: String,
    val expiresAt: String,
)

@Serializable
data class CommandAckRequest(
    val completionStatus: AgentCommandCompletionStatus,
    val handledAtUtc: String? = null,
    val failureCode: String? = null,
    val failureMessage: String? = null,
    val result: JsonElement? = null,
)

@Serializable
data class CommandAckResponse(
    val commandId: String,
    val status: AgentCommandStatus,
    val acknowledgedAt: String,
    val duplicate: Boolean,
)

sealed class CloudCommandPollResult {
    data class Success(
        val response: EdgeCommandPollResponse,
        /** Cloud's peer directory version from X-Peer-Directory-Version header. */
        val peerDirectoryVersion: Long? = null,
    ) : CloudCommandPollResult()
    data object Unauthorized : CloudCommandPollResult()
    data class Forbidden(val errorCode: String?) : CloudCommandPollResult()
    /** HTTP 404 — FEATURE_DISABLED (permanent, do not retry). */
    data class NotFound(val errorCode: String?, val message: String?) : CloudCommandPollResult()
    data class RateLimited(val retryAfterSeconds: Long?) : CloudCommandPollResult()
    data class TransportError(val message: String) : CloudCommandPollResult()
}

sealed class CloudCommandAckResult {
    data class Success(val response: CommandAckResponse) : CloudCommandAckResult()
    data object Unauthorized : CloudCommandAckResult()
    data class Forbidden(val errorCode: String?) : CloudCommandAckResult()
    /** HTTP 404 — COMMAND_NOT_FOUND (permanent, do not retry). */
    data class NotFound(val errorCode: String?, val message: String?) : CloudCommandAckResult()
    data class Conflict(val errorCode: String?, val message: String?) : CloudCommandAckResult()
    data class TransportError(val message: String) : CloudCommandAckResult()
}

// ---------------------------------------------------------------------------
// Android Installation Token — POST /api/v1/agent/installations/android
// ---------------------------------------------------------------------------

@Serializable
data class AndroidInstallationUpsertRequest(
    val installationId: String,
    @Sensitive val registrationToken: String,
    val appVersion: String,
    val osVersion: String,
    val deviceModel: String,
) {
    override fun toString(): String = SensitiveFieldFilter.redactToString(this)
}

sealed class CloudInstallationUpsertResult {
    data object Success : CloudInstallationUpsertResult()
    data object Unauthorized : CloudInstallationUpsertResult()
    data class Forbidden(val errorCode: String?) : CloudInstallationUpsertResult()
    /** HTTP 404 — FEATURE_DISABLED (permanent, do not retry). */
    data class NotFound(val errorCode: String?, val message: String?) : CloudInstallationUpsertResult()
    /** HTTP 409 — INSTALLATION_OWNERSHIP_CONFLICT (permanent, do not retry). */
    data class Conflict(val errorCode: String?, val message: String?) : CloudInstallationUpsertResult()
    data class TransportError(val message: String) : CloudInstallationUpsertResult()
}

/**
 * Data-only Android push hints are best-effort accelerators only.
 * Poll/config fetch remains authoritative even if a hint is missed or duplicated.
 */
object PushHintKinds {
    const val COMMAND_PENDING = "command_pending"
    const val CONFIG_CHANGED = "config_changed"
}

@Serializable
data class AndroidPushHintPayload(
    val kind: String,
    val deviceId: String,
    val commandCount: Int? = null,
    val configVersion: String? = null,
)

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
    /** Current site leader epoch for cloud stale-writer fencing. */
    val leaderEpoch: Long? = null,
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
    data class Success(
        val response: PreAuthForwardResponse,
        /** Cloud's peer directory version from X-Peer-Directory-Version header. */
        val peerDirectoryVersion: Long? = null,
    ) : CloudPreAuthForwardResult()

    /** HTTP 401 — token expired; caller should refresh and retry. */
    data object Unauthorized : CloudPreAuthForwardResult()

    /** HTTP 403 — forbidden (possibly decommissioned). */
    data class Forbidden(val errorCode: String?) : CloudPreAuthForwardResult()

    /** HTTP 400 — validation failure (permanent, do not retry). */
    data class BadRequest(val errorCode: String?, val message: String?) : CloudPreAuthForwardResult()

    /** M-15: HTTP 429 — rate limited by cloud. */
    data class RateLimited(val retryAfterSeconds: Long?) : CloudPreAuthForwardResult()

    /** HTTP 409 — caller must inspect [errorCode] to distinguish terminal vs retryable conflicts. */
    data class Conflict(val errorCode: String?, val message: String?) : CloudPreAuthForwardResult()

    /** Network or non-2xx/401/403/409 failure. */
    data class TransportError(val message: String) : CloudPreAuthForwardResult()
}

// ---------------------------------------------------------------------------
// Version Check — GET /api/v1/agent/version-check
// ---------------------------------------------------------------------------

/**
 * Response from GET /api/v1/agent/version-check.
 * Matches VersionCheckResponse schema from cloud API.
 */
@Serializable
data class VersionCheckResponse(
    val compatible: Boolean,
    val minimumVersion: String,
    val latestVersion: String,
    val updateRequired: Boolean,
    val updateUrl: String? = null,
    val agentVersion: String,
    val updateAvailable: Boolean,
    val releaseNotes: String? = null,
)

/** Result of calling GET /api/v1/agent/version-check. */
sealed class CloudVersionCheckResult {
    /** HTTP 200 — version check response received. */
    data class Success(val response: VersionCheckResponse) : CloudVersionCheckResult()

    /** HTTP 401 — token expired; caller should refresh and retry. */
    data object Unauthorized : CloudVersionCheckResult()

    /** HTTP 400 — validation error (e.g. missing agentVersion param). */
    data class BadRequest(val errorCode: String?, val message: String?) : CloudVersionCheckResult()

    /** HTTP 500 — server-side configuration error. */
    data class ServerError(val message: String) : CloudVersionCheckResult()

    /** Network or unexpected HTTP error. */
    data class TransportError(val message: String) : CloudVersionCheckResult()
}

/** Result of submitting telemetry to cloud. */
sealed class CloudTelemetryResult {
    /** HTTP 204 — telemetry accepted. */
    data object Success : CloudTelemetryResult()

    /** HTTP 401 — token expired. */
    data object Unauthorized : CloudTelemetryResult()

    /** HTTP 403 — forbidden (possibly decommissioned). */
    data class Forbidden(val errorCode: String?) : CloudTelemetryResult()

    /** HTTP 400 — malformed payload (permanent, do not retry). */
    data class BadRequest(val errorCode: String?, val message: String?) : CloudTelemetryResult()

    /** HTTP 404 — device not found (permanent, do not retry). */
    data class NotFound(val errorCode: String?, val message: String?) : CloudTelemetryResult()

    /** M-15: HTTP 429 — rate limited by cloud. */
    data class RateLimited(val retryAfterSeconds: Long?) : CloudTelemetryResult()

    /** Network or non-2xx/401/403 failure. */
    data class TransportError(val message: String) : CloudTelemetryResult()
}

// ---------------------------------------------------------------------------
// Diagnostic log upload — POST /api/v1/agent/diagnostic-logs
// ---------------------------------------------------------------------------

/**
 * Request payload for uploading diagnostic log entries (WARN/ERROR/FATAL).
 * Max 200 entries per batch. Only uploaded when config.telemetry.includeDiagnosticsLogs is true.
 */
@Serializable
data class DiagnosticLogUploadRequest(
    val deviceId: String,
    val siteCode: String,
    val legalEntityId: String,
    val uploadedAtUtc: String,
    val logEntries: List<String>,
)

// ---------------------------------------------------------------------------
// Site data uploads (Phase 8) — BNA, totals, prices, pump control history
// ---------------------------------------------------------------------------

/** POST /api/v1/sites/{siteCode}/bna-reports */
@Serializable
data class BnaReportBatchUpload(
    val reports: List<BnaReportUploadItem>,
)

@Serializable
data class BnaReportUploadItem(
    val terminalId: String? = null,
    val notesAccepted: Int = 0,
    val reportedAtUtc: String? = null,
)

/** POST /api/v1/sites/{siteCode}/pump-totals */
@Serializable
data class PumpTotalsBatchUpload(
    val totals: List<PumpTotalsUploadItem>,
)

@Serializable
data class PumpTotalsUploadItem(
    val pumpNumber: Int,
    val totalVolumeMicrolitres: Long,
    val totalAmountMinorUnits: Long,
    val observedAtUtc: String? = null,
)

/** POST /api/v1/sites/{siteCode}/pump-control-history */
@Serializable
data class PumpControlHistoryBatchUpload(
    val events: List<PumpControlHistoryUploadItem>,
)

@Serializable
data class PumpControlHistoryUploadItem(
    val pumpNumber: Int,
    val actionType: String? = null,
    val source: String? = null,
    val note: String? = null,
    val actionAtUtc: String? = null,
)

/** POST /api/v1/sites/{siteCode}/price-snapshots */
@Serializable
data class PriceSnapshotBatchUpload(
    val snapshots: List<PriceSnapshotUploadItem>,
)

@Serializable
data class PriceSnapshotUploadItem(
    val priceSetId: String? = null,
    val gradeId: String? = null,
    val gradeName: String? = null,
    val priceMinorUnits: Long = 0,
    val currencyCode: String? = null,
    val observedAtUtc: String? = null,
)
