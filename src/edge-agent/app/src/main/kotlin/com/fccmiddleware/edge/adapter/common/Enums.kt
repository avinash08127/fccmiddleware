package com.fccmiddleware.edge.adapter.common

/** Canonical transaction lifecycle status. Mirrors cloud TransactionStatus. */
enum class TransactionStatus {
    PENDING, SYNCED, SYNCED_TO_ODOO, STALE_PENDING, DUPLICATE, ARCHIVED
}

/**
 * Pre-authorization lifecycle status.
 * Terminal states: COMPLETED, CANCELLED, EXPIRED, FAILED.
 */
enum class PreAuthStatus {
    PENDING, AUTHORIZED, DISPENSING, COMPLETED, CANCELLED, EXPIRED, FAILED
}

/**
 * Edge-only sync state for buffered transactions.
 * Progression: PENDING → UPLOADED → SYNCED_TO_ODOO → ARCHIVED → (deleted)
 *              PENDING → DEAD_LETTER → (deleted after retention)
 * Separate from TransactionStatus per state machine spec §5.3.
 */
enum class SyncStatus {
    PENDING, UPLOADED, SYNCED_TO_ODOO, ARCHIVED, DEAD_LETTER
}

/** Site ingestion mode from site config. */
enum class IngestionMode {
    CLOUD_DIRECT, RELAY, BUFFER_ALWAYS
}

/** FCC hardware vendor. */
enum class FccVendor {
    DOMS, RADIX, ADVATEC, PETRONITE
}

/** Connectivity state machine states. */
enum class ConnectivityState {
    FULLY_ONLINE, INTERNET_DOWN, FCC_UNREACHABLE, FULLY_OFFLINE
}

/** Pump operational state. */
enum class PumpState {
    IDLE, AUTHORIZED, CALLING, DISPENSING, PAUSED, COMPLETED, ERROR, OFFLINE, UNKNOWN
}

/** Source of a pump status snapshot. */
enum class PumpStatusSource {
    FCC_LIVE, EDGE_SYNTHESIZED
}

/** Adapter-declared pump status capability level. */
enum class PumpStatusCapability {
    /** Real-time FCC pump status (DOMS). */
    LIVE,
    /** Edge-synthesized from available data (Petronite). */
    SYNTHESIZED,
    /** Protocol does not support pump status (Radix). */
    NOT_SUPPORTED,
    /** Device type does not have pumps (Advatec fiscal). */
    NOT_APPLICABLE,
}

/** Which ingestion path delivered a transaction. */
enum class IngestionSource {
    FCC_PUSH, EDGE_UPLOAD, CLOUD_PULL
}

/** Reconciliation outcome for pre-auth-matched transactions. */
enum class ReconciliationStatus {
    MATCHED, VARIANCE_WITHIN_TOLERANCE, VARIANCE_FLAGGED, UNMATCHED
}

/** Outcome status of a pre-auth command sent to the FCC. */
enum class PreAuthResultStatus {
    AUTHORIZED, DECLINED, TIMEOUT, ERROR,

    /**
     * Returned when a duplicate pre-auth request arrives for an order that already has
     * an in-flight (PENDING) request. Odoo should treat this as "wait and retry" rather
     * than a permanent failure.
     */
    IN_PROGRESS,
}
