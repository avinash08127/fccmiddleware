package com.fccmiddleware.edge.sync

import android.util.Log

/**
 * CloudUploadWorker — uploads buffered transactions to the cloud backend.
 *
 * Upload order: created_at ASC (oldest first). Never skip a failed record.
 * Replay throughput target on stable internet: >= 600 transactions/minute.
 *
 * Triggered by CadenceController when internet is UP.
 * Runs under the foreground service scope (NOT WorkManager).
 *
 * Method signatures defined here for CadenceController integration (EA-2.3).
 * Full implementation follows EA-2.x tasks.
 */
class CloudUploadWorker {

    companion object {
        private const val TAG = "CloudUploadWorker"
    }

    /**
     * Upload PENDING buffered transactions to cloud in chronological order.
     * Never skip past a failed record (replay ordering guarantee).
     * TODO (EA-2.x): implement batch upload with retry and chronological ordering
     */
    suspend fun uploadPendingBatch() {
        Log.d(TAG, "uploadPendingBatch() — stub")
    }

    /**
     * Poll cloud for transactions confirmed SYNCED_TO_ODOO.
     * Shares the cadence loop with cloud health checks (per spec §5.4).
     * TODO (EA-2.x): implement SYNCED_TO_ODOO status poll
     */
    suspend fun pollSyncedToOdooStatus() {
        Log.d(TAG, "pollSyncedToOdooStatus() — stub")
    }

    /**
     * Send accumulated telemetry metrics to cloud.
     * Piggybacks on an existing successful cloud cycle (never permanently hot).
     * TODO (EA-2.x): implement telemetry reporting
     */
    suspend fun reportTelemetry() {
        Log.d(TAG, "reportTelemetry() — stub")
    }

    /**
     * Poll cloud for configuration updates.
     * Suspended when internet is DOWN; uses last-known config in that case.
     * TODO (EA-2.x): implement config poll and hot-reload
     */
    suspend fun pollConfig() {
        Log.d(TAG, "pollConfig() — stub")
    }
}
