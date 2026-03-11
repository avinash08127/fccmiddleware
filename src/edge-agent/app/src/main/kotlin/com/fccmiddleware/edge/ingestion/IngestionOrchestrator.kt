package com.fccmiddleware.edge.ingestion

import android.util.Log

/**
 * IngestionOrchestrator — coordinates FCC polling, normalization, and local buffering.
 *
 * Supports ingestion modes from site config:
 *   - CLOUD_DIRECT: FCC pushes directly to cloud; agent is safety-net LAN poller
 *   - RELAY: Agent is primary receiver; polls FCC, buffers, uploads to cloud
 *   - BUFFER_ALWAYS: Agent always buffers locally first, then uploads
 *
 * Offline-first guarantee: every polled transaction is buffered locally before
 * any upload attempt. No transaction is lost on connectivity failure.
 *
 * Method signatures defined here for CadenceController integration (EA-2.3).
 * Full implementation follows EA-2.x tasks.
 */
class IngestionOrchestrator {

    companion object {
        private const val TAG = "IngestionOrchestrator"
    }

    /**
     * Poll FCC for new transactions, normalize, and buffer locally.
     * Only invoked when FCC is reachable (FULLY_ONLINE or INTERNET_DOWN).
     * TODO (EA-2.x): implement poll → normalize → buffer pipeline
     */
    suspend fun poll() {
        Log.d(TAG, "poll() — stub")
    }

    /**
     * Trigger an immediate FCC pull for a specific pump (manual pull from Odoo POS).
     * Core requirement — treated as a first-class feature, not optional convenience.
     * TODO (EA-2.x): implement manual FCC pull trigger
     */
    suspend fun pollNow(pumpNumber: Int? = null) {
        Log.d(TAG, "pollNow(pumpNumber=$pumpNumber) — stub")
    }
}
