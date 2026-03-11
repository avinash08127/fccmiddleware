package com.fccmiddleware.edge.sync

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Environment
import android.os.StatFs
import android.os.SystemClock
import android.util.Log
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import java.io.File
import java.time.Instant
import java.util.concurrent.atomic.AtomicInteger

/**
 * TelemetryReporter — assembles and reports device/agent health metrics to cloud.
 *
 * ## Telemetry fields
 * - Device: battery, storage, memory, app version, uptime, OS, model
 * - FCC health: reachability, heartbeat age, vendor/host/port, consecutive failures
 * - Buffer: per-status counts, oldest PENDING timestamp, DB file size
 * - Sync: last upload/poll timestamps, sync lag, config version, batch size
 * - Error counts: accumulated since last successful submission, reset to 0 on success
 *
 * ## Behaviour
 * - Fire-and-forget: if submission fails, the payload is discarded (no buffering).
 * - Error counters are reset to 0 only after a successful HTTP 204 from cloud.
 * - Sequence number is monotonic per device, persisted in [SyncState.telemetrySequence].
 * - Called by [CloudUploadWorker.reportTelemetry] on the cadence schedule.
 */
class TelemetryReporter(
    private val context: Context,
    private val transactionDao: TransactionBufferDao,
    private val syncStateDao: SyncStateDao,
    private val connectivityManager: ConnectivityManager,
    private val configManager: ConfigManager,
    private val cloudUploadWorker: CloudUploadWorkerRef? = null,
    private val appVersion: String = "1.0.0",
    /** Epoch millis when the foreground service started, for uptime calculation. */
    private val serviceStartTimeMs: Long = SystemClock.elapsedRealtime(),
    private val databasePath: String? = null,
) {

    companion object {
        private const val TAG = "TelemetryReporter"
    }

    // -------------------------------------------------------------------------
    // Error counters — accumulated since last successful telemetry submission.
    // Thread-safe via AtomicInteger; reset to 0 after successful send.
    // -------------------------------------------------------------------------

    val fccConnectionErrors = AtomicInteger(0)
    val cloudUploadErrors = AtomicInteger(0)
    val cloudAuthErrors = AtomicInteger(0)
    val localApiErrors = AtomicInteger(0)
    val bufferWriteErrors = AtomicInteger(0)
    val adapterNormalizationErrors = AtomicInteger(0)
    val preAuthErrors = AtomicInteger(0)

    /**
     * Assemble the full telemetry payload from all data sources.
     *
     * Returns null if required identity fields (deviceId, siteCode, legalEntityId)
     * are not available from config (agent not yet provisioned).
     */
    suspend fun buildPayload(): TelemetryPayload? {
        val cfg = configManager.config.value
        if (cfg == null) {
            Log.d(TAG, "buildPayload() — no config loaded, skipping telemetry")
            return null
        }

        val deviceId = cfg.agent.deviceId
        val siteCode = cfg.site.siteCode
        val legalEntityId = cfg.site.legalEntityId

        val sequence = nextSequenceNumber()

        return TelemetryPayload(
            deviceId = deviceId,
            siteCode = siteCode,
            legalEntityId = legalEntityId,
            reportedAtUtc = Instant.now().toString(),
            sequenceNumber = sequence,
            connectivityState = connectivityManager.state.value.name,
            device = collectDeviceStatus(),
            fccHealth = collectFccHealth(cfg),
            buffer = collectBufferStatus(),
            sync = collectSyncStatus(cfg),
            errorCounts = snapshotErrorCounts(),
        )
    }

    /**
     * Reset all error counters to zero.
     * Called after a successful telemetry submission (HTTP 204).
     */
    fun resetErrorCounts() {
        fccConnectionErrors.set(0)
        cloudUploadErrors.set(0)
        cloudAuthErrors.set(0)
        localApiErrors.set(0)
        bufferWriteErrors.set(0)
        adapterNormalizationErrors.set(0)
        preAuthErrors.set(0)
    }

    // -------------------------------------------------------------------------
    // Device metrics
    // -------------------------------------------------------------------------

    private fun collectDeviceStatus(): DeviceStatusDto {
        val batteryIntent = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val batteryLevel = batteryIntent?.let { intent ->
            val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
            val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
            if (level >= 0 && scale > 0) (level * 100) / scale else 0
        } ?: 0
        val isCharging = batteryIntent?.let { intent ->
            val plugged = intent.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0)
            plugged != 0
        } ?: false

        val stat = StatFs(Environment.getDataDirectory().path)
        val storageFreeMb = (stat.availableBytes / (1024L * 1024L)).toInt()
        val storageTotalMb = (stat.totalBytes / (1024L * 1024L)).toInt()

        val runtime = Runtime.getRuntime()
        val memoryFreeMb = (runtime.freeMemory() / (1024L * 1024L)).toInt()
        val memoryTotalMb = (runtime.totalMemory() / (1024L * 1024L)).toInt()

        val uptimeSeconds = ((SystemClock.elapsedRealtime() - serviceStartTimeMs) / 1000L).toInt()

        return DeviceStatusDto(
            batteryPercent = batteryLevel,
            isCharging = isCharging,
            storageFreeMb = storageFreeMb,
            storageTotalMb = storageTotalMb,
            memoryFreeMb = memoryFreeMb,
            memoryTotalMb = memoryTotalMb,
            appVersion = appVersion,
            appUptimeSeconds = uptimeSeconds.coerceAtLeast(0),
            osVersion = android.os.Build.VERSION.RELEASE,
            deviceModel = android.os.Build.MODEL,
        )
    }

    // -------------------------------------------------------------------------
    // FCC health
    // -------------------------------------------------------------------------

    private fun collectFccHealth(
        cfg: com.fccmiddleware.edge.config.EdgeAgentConfigDto,
    ): FccHealthStatusDto {
        val state = connectivityManager.state.value
        val isReachable = state == ConnectivityState.FULLY_ONLINE ||
            state == ConnectivityState.INTERNET_DOWN

        val heartbeatAgeSeconds = connectivityManager.fccHeartbeatAgeSeconds()
        val lastFccSuccessMs = connectivityManager.lastFccSuccessMs
        val lastHeartbeatAtUtc = if (lastFccSuccessMs > 0L) {
            Instant.ofEpochMilli(lastFccSuccessMs).toString()
        } else {
            null
        }

        return FccHealthStatusDto(
            isReachable = isReachable,
            lastHeartbeatAtUtc = lastHeartbeatAtUtc,
            heartbeatAgeSeconds = heartbeatAgeSeconds,
            fccVendor = cfg.fccConnection.vendor,
            fccHost = cfg.fccConnection.host,
            fccPort = cfg.fccConnection.port,
            consecutiveHeartbeatFailures = fccConnectionErrors.get(),
        )
    }

    // -------------------------------------------------------------------------
    // Buffer status
    // -------------------------------------------------------------------------

    private suspend fun collectBufferStatus(): BufferStatusDto {
        val statusCounts = try {
            transactionDao.countByStatus()
        } catch (e: Exception) {
            Log.w(TAG, "Failed to query buffer status counts", e)
            emptyList()
        }

        val countMap = statusCounts.associate { it.syncStatus to it.count }
        val pendingCount = countMap["PENDING"] ?: 0
        val uploadedCount = countMap["UPLOADED"] ?: 0
        val syncedToOdooCount = countMap["SYNCED_TO_ODOO"] ?: 0
        val failedCount = countMap["FAILED"] ?: 0
        val totalRecords = countMap.values.sum()

        val oldestPendingAtUtc = try {
            transactionDao.oldestPendingCreatedAt()
        } catch (e: Exception) {
            Log.w(TAG, "Failed to query oldest pending timestamp", e)
            null
        }

        val bufferSizeMb = try {
            val dbPath = databasePath ?: context.getDatabasePath("edge_buffer.db").absolutePath
            val dbFile = File(dbPath)
            if (dbFile.exists()) (dbFile.length() / (1024L * 1024L)).toInt() else 0
        } catch (e: Exception) {
            0
        }

        return BufferStatusDto(
            totalRecords = totalRecords,
            pendingUploadCount = pendingCount,
            syncedCount = uploadedCount,
            syncedToOdooCount = syncedToOdooCount,
            failedCount = failedCount,
            oldestPendingAtUtc = oldestPendingAtUtc,
            bufferSizeMb = bufferSizeMb,
        )
    }

    // -------------------------------------------------------------------------
    // Sync status
    // -------------------------------------------------------------------------

    private suspend fun collectSyncStatus(
        cfg: com.fccmiddleware.edge.config.EdgeAgentConfigDto,
    ): SyncStatusDto {
        val syncState = try {
            syncStateDao.get()
        } catch (e: Exception) {
            Log.w(TAG, "Failed to read SyncState", e)
            null
        }

        val oldestPendingAtUtc = try {
            transactionDao.oldestPendingCreatedAt()
        } catch (_: Exception) {
            null
        }
        val syncLagSeconds = if (oldestPendingAtUtc != null) {
            try {
                val oldest = Instant.parse(oldestPendingAtUtc)
                ((Instant.now().epochSecond - oldest.epochSecond).toInt()).coerceAtLeast(0)
            } catch (_: Exception) {
                null
            }
        } else {
            null
        }

        return SyncStatusDto(
            lastSyncAttemptUtc = syncState?.lastUploadAt,
            lastSuccessfulSyncUtc = syncState?.lastUploadAt,
            syncLagSeconds = syncLagSeconds,
            lastStatusPollUtc = syncState?.lastStatusPollAt,
            lastConfigPullUtc = syncState?.lastConfigPullAt,
            configVersion = syncState?.lastConfigVersion?.toString(),
            uploadBatchSize = cfg.sync.uploadBatchSize,
        )
    }

    // -------------------------------------------------------------------------
    // Error counts snapshot
    // -------------------------------------------------------------------------

    private fun snapshotErrorCounts(): ErrorCountsDto = ErrorCountsDto(
        fccConnectionErrors = fccConnectionErrors.get(),
        cloudUploadErrors = cloudUploadErrors.get(),
        cloudAuthErrors = cloudAuthErrors.get(),
        localApiErrors = localApiErrors.get(),
        bufferWriteErrors = bufferWriteErrors.get(),
        adapterNormalizationErrors = adapterNormalizationErrors.get(),
        preAuthErrors = preAuthErrors.get(),
    )

    // -------------------------------------------------------------------------
    // Sequence number (monotonic, persisted in SyncState)
    // -------------------------------------------------------------------------

    private suspend fun nextSequenceNumber(): Long {
        return try {
            val current = syncStateDao.get()
            val next = (current?.telemetrySequence ?: 0L) + 1L
            val now = Instant.now().toString()
            val updated = current?.copy(telemetrySequence = next, updatedAt = now)
                ?: com.fccmiddleware.edge.buffer.entity.SyncState(
                    lastFccCursor = null,
                    lastUploadAt = null,
                    lastStatusPollAt = null,
                    lastConfigPullAt = null,
                    lastConfigVersion = null,
                    telemetrySequence = next,
                    updatedAt = now,
                )
            syncStateDao.upsert(updated)
            next
        } catch (e: Exception) {
            Log.w(TAG, "Failed to increment telemetry sequence number", e)
            1L
        }
    }
}

/**
 * Thin reference to [CloudUploadWorker] to avoid circular dependency.
 * Provides the upload batch size for telemetry reporting.
 */
interface CloudUploadWorkerRef {
    val uploadBatchSize: Int
}
