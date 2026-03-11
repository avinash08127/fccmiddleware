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
 * Separate from TransactionStatus per state machine spec §5.3.
 */
enum class SyncStatus {
    PENDING, UPLOADED, SYNCED_TO_ODOO, ARCHIVED
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
    AUTHORIZED, DECLINED, TIMEOUT, ERROR
}
