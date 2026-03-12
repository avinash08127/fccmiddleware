package com.fccmiddleware.edge.adapter.common

import com.fccmiddleware.edge.security.Sensitive
import kotlinx.serialization.Serializable

// ---------------------------------------------------------------------------
// Adapter input/output supporting types — per tier-1-5-fcc-adapter-interface-contracts.md §5.2
// ---------------------------------------------------------------------------

/**
 * Wraps a raw FCC payload with vendor/context metadata for adapter processing.
 *
 * vendor must match the resolved site/FCC config; mismatch is a non-recoverable
 * validation error.
 */
@Serializable
data class RawPayloadEnvelope(
    /** Must match resolved site/FCC config vendor. */
    val vendor: FccVendor,

    /** Adapter context key for mappings and validation. */
    val siteCode: String,

    /** Time payload reached the Edge boundary. UTC ISO 8601. */
    val receivedAtUtc: String,

    /**
     * MIME content type. MVP values: "application/json", "text/xml",
     * "application/octet-stream". DOMS uses "application/json".
     */
    val contentType: String,

    /** Exact raw payload, unchanged. */
    val payload: String,
)

/**
 * Cursor input for fetchTransactions.
 *
 * Provide cursorToken when the vendor returned one on the previous call,
 * otherwise provide sinceUtc as inclusive lower bound.
 */
@Serializable
data class FetchCursor(
    /** Vendor opaque continuation token. */
    val cursorToken: String? = null,

    /** Inclusive lower bound when vendor token is unavailable. UTC ISO 8601. */
    val sinceUtc: String? = null,

    /** Caller hint for page size; adapter may reduce but must not exceed max. */
    val limit: Int = 50,
)

/**
 * Batch of canonical transactions returned by fetchTransactions.
 *
 * transactions may be empty with hasMore=false — valid no-data poll result.
 */
@Serializable
data class TransactionBatch(
    /** Normalised transactions. May be empty. */
    val transactions: List<CanonicalTransaction>,

    /** hasMore=true when an immediate follow-up fetch should continue. */
    val hasMore: Boolean,

    /** Vendor opaque token for the next fetch call. Null when unavailable. */
    val nextCursorToken: String? = null,

    /** Returned when cursor progression is time-based. UTC ISO 8601. */
    val highWatermarkUtc: String? = null,

    /** Vendor batch/message identifier for diagnostics. */
    val sourceBatchId: String? = null,
)

/**
 * Pre-auth command sent to the FCC over LAN.
 *
 * customerTaxId is PII — NEVER log this field.
 * Cloud forwarding from the result is always asynchronous; never on the request path.
 */
@Serializable
data class PreAuthCommand(
    /** Used to resolve FCC config and mappings. */
    val siteCode: String,

    /** Physical pump number. */
    val pumpNumber: Int,

    /** Authorized amount in minor currency units. */
    val amountMinorUnits: Long,

    /** Price per litre in minor currency units at pre-auth creation time. */
    val unitPrice: Long,

    /** Must match site config. */
    val currencyCode: String,

    /** Required when FCC needs explicit nozzle selection. */
    val nozzleNumber: Int? = null,

    /** Echo field for later correlation when vendor supports it. */
    val odooOrderId: String? = null,

    /** Required when site fiscalization config requires it. PII — NEVER log. */
    @Sensitive val customerTaxId: String? = null,

    /** Radix: Customer name — maps to CUSTNAME. */
    val customerName: String? = null,

    /** Radix: Customer ID type — maps to CUSTIDTYPE (1=TIN, 2=DrivingLicense, etc.). */
    val customerIdType: Int? = null,

    /** Radix: Customer phone — maps to MOBILENUM. */
    val customerPhone: String? = null,
)

/**
 * Canonical outcome of a pre-auth command.
 *
 * status=AUTHORIZED is the only success state; all others require caller handling.
 */
@Serializable
data class PreAuthResult(
    /** AUTHORIZED, DECLINED, TIMEOUT, or ERROR. */
    val status: PreAuthResultStatus,

    /** Vendor reference when status=AUTHORIZED. */
    val authorizationCode: String? = null,

    /** FCC-provided authorization expiry. UTC ISO 8601. */
    val expiresAtUtc: String? = null,

    /** Operator-safe outcome detail; never contains PII. */
    val message: String? = null,

    /**
     * FCC-assigned correlation ID linking this pre-auth to subsequent dispensing transactions.
     * Used for reconciliation between edge pre-auth records and FCC transactions.
     * Stored in PreAuthRecord.fccCorrelationId for later cloud forwarding.
     */
    val correlationId: String? = null,
)

/**
 * Command to cancel/deauthorize an active pre-authorization on the FCC.
 *
 * Vendor-specific adapters use different fields:
 * - DOMS JPL: pumpNumber → deauthorize_Fp_req with FpId
 * - Radix: pumpNumber + fccCorrelationId → AUTH_DATA with AUTH=FALSE
 * - Petronite: fccCorrelationId → POST /{orderId}/cancel
 */
@Serializable
data class CancelPreAuthCommand(
    /** Used to resolve FCC config and mappings. */
    val siteCode: String,

    /** Physical pump number (FCC-mapped). Required for DOMS and Radix. */
    val pumpNumber: Int,

    /** Nozzle number when FCC needs it (DOMS). */
    val nozzleNumber: Int? = null,

    /**
     * FCC-assigned correlation ID from the original pre-auth.
     * Required for Petronite (OrderId) and Radix (RADIX-TOKEN-xxx).
     */
    val fccCorrelationId: String? = null,
)

// ---------------------------------------------------------------------------
// Normalization result — sealed outcome of IFccAdapter.normalize()
// ---------------------------------------------------------------------------

/**
 * Sealed result of [IFccAdapter.normalize].
 *
 * Adapters must return [Success] or [Failure] — never throw exceptions.
 */
sealed class NormalizationResult {
    /** Normalization succeeded. */
    data class Success(val transaction: CanonicalTransaction) : NormalizationResult()

    /**
     * Normalization failed.
     *
     * @param errorCode Machine-readable code: UNSUPPORTED_MESSAGE_TYPE, INVALID_PAYLOAD,
     *   MISSING_REQUIRED_FIELD, or MALFORMED_FIELD.
     * @param message Human-readable detail (never contains PII).
     * @param fieldName Optional field path that caused the failure (e.g. "amount", "startedAt").
     */
    data class Failure(
        val errorCode: String,
        val message: String,
        val fieldName: String? = null,
    ) : NormalizationResult()
}

// ---------------------------------------------------------------------------
// Factory configuration input
// ---------------------------------------------------------------------------

/**
 * Runtime configuration supplied to FccAdapterFactory.resolve().
 *
 * Minimum required fields per §5.4 of adapter interface contracts spec.
 * productCodeMapping: maps raw FCC product codes to canonical codes (e.g. "001" → "PMS").
 */
@Serializable
data class AgentFccConfig(
    val fccVendor: FccVendor,
    val connectionProtocol: String,
    val hostAddress: String,
    val port: Int,

    /** API key or other auth credential. Stored in EncryptedSharedPreferences — NEVER log. */
    @Sensitive val authCredential: String,

    val ingestionMode: IngestionMode,
    val pullIntervalSeconds: Int,

    /** Site identifier (e.g. "TZ-DAR-001"). Used in dedup keys and envelope metadata. */
    val siteCode: String = "",

    /** Maps raw FCC product codes → canonical product codes. */
    val productCodeMapping: Map<String, String>,

    /** IANA timezone identifier for the site (e.g. "Africa/Johannesburg"). */
    val timezone: String,

    val currencyCode: String,

    /**
     * Offset added to raw FCC pump numbers to produce canonical pump numbers.
     * Allows normalising FCC-internal numbering to Odoo POS numbering.
     */
    val pumpNumberOffset: Int = 0,

    /** Radix: SHA-1 signing password for message authentication. */
    @Sensitive val sharedSecret: String? = null,

    /** Radix: Unique Station Number (1–999999). */
    val usnCode: Int? = null,

    /** Radix: External Authorization port; transaction port = authPort + 1. */
    val authPort: Int? = null,

    /** Radix: JSON string mapping canonical pump numbers to (PUMP_ADDR, FP) pairs. */
    val fccPumpAddressMap: String? = null,

    // ── DOMS TCP/JPL fields ──────────────────────────────────────────────────

    /** DOMS TCP: JPL binary-framed port number. */
    val jplPort: Int? = null,

    /** DOMS TCP: FcLogon access code credential. */
    @Sensitive val fcAccessCode: String? = null,

    /** DOMS TCP: Country code for locale-specific formatting. */
    val domsCountryCode: String? = null,

    /** DOMS TCP: POS version identifier sent during FcLogon handshake. */
    val posVersionId: String? = null,

    /** DOMS TCP: Heartbeat interval in seconds (default 30). */
    val heartbeatIntervalSeconds: Int? = null,

    /** DOMS TCP: Maximum reconnection backoff in seconds. */
    val reconnectBackoffMaxSeconds: Int? = null,

    /** DOMS TCP: Comma-separated list of configured pump numbers (e.g., "1,2,3,4"). */
    val configuredPumps: String? = null,

    // ── Petronite OAuth2 fields ──────────────────────────────────────────────

    /** Petronite: OAuth2 client ID for Client Credentials flow. */
    @Sensitive val clientId: String? = null,

    /** Petronite: OAuth2 client secret for Client Credentials flow. */
    @Sensitive val clientSecret: String? = null,

    /** Petronite: Webhook HMAC secret for payload validation. */
    @Sensitive val webhookSecret: String? = null,

    /** Petronite: OAuth2 token endpoint URL. */
    val oauthTokenEndpoint: String? = null,

    // ── Advatec EFD fields ──────────────────────────────────────────────────

    /** Advatec: Device host address (default "127.0.0.1" — Advatec runs on localhost). */
    val advatecDeviceAddress: String? = null,

    /** Advatec: Device HTTP port (default 5560). */
    val advatecDevicePort: Int? = null,

    /** Advatec: Port for the local webhook listener that receives Receipt callbacks. */
    val advatecWebhookListenerPort: Int? = null,

    /** Advatec: Shared token for webhook URL authentication. */
    @Sensitive val advatecWebhookToken: String? = null,

    /** Advatec: TRA-registered EFD serial number for validation (e.g., "10TZ101807"). */
    val advatecEfdSerialNumber: String? = null,

    /** Advatec: Default CustIdType for Customer submissions (1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL). */
    val advatecCustIdType: Int? = null,
)
