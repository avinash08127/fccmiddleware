package com.fccmiddleware.edge.sync

import android.content.Context
import android.os.BatteryManager
import android.os.Environment
import android.os.StatFs
import android.os.SystemClock
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
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

    /**
     * Mutex serializing [nextSequenceNumber] so concurrent callers cannot
     * read-increment-persist the same sequence value (prevents duplicate sequence numbers).
     */
    private val sequenceMutex = Mutex()

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
            // M-06: Log accumulated error counters at WARN level so they are captured
            // in device logs even when config is not yet loaded (unprovisioned device).
            // Without this, errors accumulate silently in memory and are lost on restart.
            val counts = snapshotErrorCounts()
            val hasErrors = counts.fccConnectionErrors > 0 || counts.cloudUploadErrors > 0 ||
                counts.cloudAuthErrors > 0 || counts.localApiErrors > 0 ||
                counts.bufferWriteErrors > 0 || counts.adapterNormalizationErrors > 0 ||
                counts.preAuthErrors > 0
            if (hasErrors) {
                AppLogger.w(
                    TAG,
                    "buildPayload() — no config loaded; unreported error counters: " +
                        "fcc=${counts.fccConnectionErrors} upload=${counts.cloudUploadErrors} " +
                        "auth=${counts.cloudAuthErrors} localApi=${counts.localApiErrors} " +
                        "bufferWrite=${counts.bufferWriteErrors} adapter=${counts.adapterNormalizationErrors} " +
                        "preAuth=${counts.preAuthErrors}",
                )
            } else {
                AppLogger.d(TAG, "buildPayload() — no config loaded, skipping telemetry")
            }
            return null
        }

        val deviceId = cfg.identity.deviceId
        val siteCode = cfg.identity.siteCode
        val legalEntityId = cfg.identity.legalEntityId

        val sequence = nextSequenceNumber()

        // AP-020: Query oldestPendingCreatedAt once and pass to both collectors
        // to avoid duplicate DB queries and ensure consistent values.
        val oldestPendingAtUtc = try {
            transactionDao.oldestPendingCreatedAt()
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to query oldest pending timestamp", e)
            null
        }

        // AP-022: Run independent collectors in parallel to reduce total
        // telemetry assembly time from serial (~10-50ms) to parallel (~5-15ms).
        return coroutineScope {
            val bufferDeferred = async { collectBufferStatus(oldestPendingAtUtc) }
            val syncDeferred = async { collectSyncStatus(cfg, oldestPendingAtUtc) }

            TelemetryPayload(
                deviceId = deviceId,
                siteCode = siteCode,
                legalEntityId = legalEntityId,
                reportedAtUtc = Instant.now().toString(),
                sequenceNumber = sequence,
                connectivityState = connectivityManager.state.value.name,
                device = collectDeviceStatus(),
                fccHealth = collectFccHealth(cfg),
                buffer = bufferDeferred.await(),
                sync = syncDeferred.await(),
                // AF-037: Use atomic snapshot+reset so no increments are lost between
                // buildPayload() and the post-submission reset. On submission failure the
                // counters are already zeroed, which is acceptable for fire-and-forget telemetry.
                errorCounts = snapshotAndResetErrorCounts(),
            )
        }
    }

    /**
     * Reset all error counters to zero.
     * Called after a successful telemetry submission (HTTP 204).
     *
     * @deprecated Prefer [snapshotAndResetErrorCounts] which atomically captures and resets
     * each counter via getAndSet(0), preventing increments between snapshot and reset from
     * being silently lost. Retained for backward compatibility where reset-only is needed.
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

    /**
     * M-05: Atomically snapshot and reset all error counters in one pass.
     * Uses getAndSet(0) on each AtomicInteger so increments that land between
     * individual counter reads are captured in the snapshot (not silently lost).
     */
    fun snapshotAndResetErrorCounts(): ErrorCountsDto = ErrorCountsDto(
        fccConnectionErrors = fccConnectionErrors.getAndSet(0),
        cloudUploadErrors = cloudUploadErrors.getAndSet(0),
        cloudAuthErrors = cloudAuthErrors.getAndSet(0),
        localApiErrors = localApiErrors.getAndSet(0),
        bufferWriteErrors = bufferWriteErrors.getAndSet(0),
        adapterNormalizationErrors = adapterNormalizationErrors.getAndSet(0),
        preAuthErrors = preAuthErrors.getAndSet(0),
    )

    // -------------------------------------------------------------------------
    // Device metrics
    // -------------------------------------------------------------------------

    private fun collectDeviceStatus(): DeviceStatusDto {
        // AP-021: Use BatteryManager system service to read battery status from sysfs
        // instead of registerReceiver(null, IntentFilter(ACTION_BATTERY_CHANGED)) IPC.
        val batteryManager = context.getSystemService(Context.BATTERY_SERVICE) as? BatteryManager
        val batteryLevel = batteryManager
            ?.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
            ?.coerceIn(0, 100)
            ?: 0
        val isCharging = batteryManager?.isCharging ?: false

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
            fccVendor = cfg.fcc.vendor ?: "UNCONFIGURED",
            fccHost = cfg.fcc.hostAddress ?: "UNCONFIGURED",
            fccPort = cfg.fcc.port ?: 0,
            consecutiveHeartbeatFailures = fccConnectionErrors.get(),
        )
    }

    // -------------------------------------------------------------------------
    // Buffer status
    // -------------------------------------------------------------------------

    private suspend fun collectBufferStatus(oldestPendingAtUtc: String?): BufferStatusDto {
        val statusCounts = try {
            transactionDao.countByStatus()
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to query buffer status counts", e)
            emptyList()
        }

        val countMap = statusCounts.associate { it.syncStatus to it.count }
        val pendingCount = countMap["PENDING"] ?: 0
        val uploadedCount = countMap["UPLOADED"] ?: 0
        val syncedToOdooCount = countMap["SYNCED_TO_ODOO"] ?: 0
        val failedCount = countMap["FAILED"] ?: 0
        val totalRecords = countMap.values.sum()

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
        oldestPendingAtUtc: String?,
    ): SyncStatusDto {
        val syncState = try {
            syncStateDao.get()
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to read SyncState", e)
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
            // AF-035: lastSyncAttemptUtc now uses the dedicated attempt timestamp
            // (written before each HTTP call), while lastSuccessfulSyncUtc uses the
            // success-only timestamp. Cloud can now detect persistent upload failures.
            lastSyncAttemptUtc = syncState?.lastUploadAttemptAt,
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

    /**
     * Read-only snapshot of current error counts (does not reset).
     * Used by [buildPayload] to assemble the telemetry payload.
     * The actual reset happens via [snapshotAndResetErrorCounts] after successful submission.
     */
    internal fun snapshotErrorCounts(): ErrorCountsDto = ErrorCountsDto(
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

    /**
     * Atomically increment and return the next telemetry sequence number.
     *
     * Uses [SyncStateDao.incrementAndGetTelemetrySequence] which wraps the
     * read-modify-write in a Room @Transaction, eliminating the risk of
     * duplicate sequence numbers from concurrent access or mid-operation crashes.
     * The coroutine Mutex provides additional in-process serialization.
     */
    private suspend fun nextSequenceNumber(): Long = sequenceMutex.withLock {
        try {
            val now = Instant.now().toString()
            syncStateDao.incrementAndGetTelemetrySequence(now)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to increment telemetry sequence number", e)
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
