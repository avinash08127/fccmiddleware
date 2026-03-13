package com.fccmiddleware.edge.adapter.common

/**
 * M-22: Centralised timeout constants for all FCC adapters.
 *
 * Having a single source of truth ensures consistent behaviour across vendors
 * and makes site-specific tuning straightforward (replace these constants
 * with config-driven values when runtime tuning is needed).
 */
object AdapterTimeouts {
    /** Hard timeout for heartbeat probes (all adapters). */
    const val HEARTBEAT_TIMEOUT_MS = 5_000L

    /**
     * Hard timeout for pre-auth requests (adapter-internal).
     *
     * This is the timeout the adapter itself applies around a single FCC call.
     * The [PreAuthHandler] wraps the adapter call with its own (larger) timeout
     * to account for multiple retries or two-step flows.
     */
    const val PREAUTH_TIMEOUT_MS = 15_000L

    /**
     * Hard timeout for HTTP submissions to local FCC devices (Advatec).
     * Applies to both connect and read independently.
     */
    const val SUBMIT_TIMEOUT_MS = 10_000

    /**
     * Pre-auth TTL for the in-memory correlation map before entries are purged.
     * 30 minutes covers the longest reasonable pump dispensing window.
     */
    const val PRE_AUTH_TTL_MILLIS = 30L * 60 * 1000
}
