package com.fccmiddleware.edge.ui

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.dao.SiteDataDao
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.AuditLog
import com.fccmiddleware.edge.buffer.entity.SiteInfo
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.logging.StructuredFileLogger
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.time.Instant

/**
 * ViewModel for DiagnosticsActivity — owns all data fetching, transformation,
 * and refresh scheduling. The Activity only observes [snapshot] and renders.
 */
class DiagnosticsViewModel(
    private val connectivityManager: ConnectivityManager,
    private val siteDataDao: SiteDataDao,
    private val transactionDao: TransactionBufferDao,
    private val syncStateDao: SyncStateDao,
    private val auditLogDao: AuditLogDao,
    private val configManager: ConfigManager,
    private val fileLogger: StructuredFileLogger,
) : ViewModel() {

    data class DiagnosticsSnapshot(
        val connectivityState: ConnectivityState,
        val heartbeatAge: Int?,
        val configVersion: Int?,
        val bufferDepth: Int,
        val syncState: SyncState?,
        val syncLagSeconds: Int?,
        val recentLogs: List<AuditLog>,
        val siteInfo: SiteInfo?,
        val productCount: Int,
        val pumpCount: Int,
        val nozzleCount: Int,
        val structuredEntries: List<String>,
        val logSizeBytes: Long,
        val refreshedAt: String,
    )

    private val _snapshot = MutableStateFlow<DiagnosticsSnapshot?>(null)
    val snapshot: StateFlow<DiagnosticsSnapshot?> = _snapshot.asStateFlow()

    private var autoRefreshJob: kotlinx.coroutines.Job? = null

    fun startAutoRefresh() {
        if (autoRefreshJob?.isActive == true) return
        autoRefreshJob = viewModelScope.launch {
            while (isActive) {
                refresh()
                delay(REFRESH_INTERVAL_MS)
            }
        }
    }

    fun stopAutoRefresh() {
        autoRefreshJob?.cancel()
        autoRefreshJob = null
    }

    suspend fun refresh() {
        val connState = connectivityManager.state.value
        val heartbeatAge = connectivityManager.fccHeartbeatAgeSeconds()
        val configVersion = configManager.currentConfigVersion

        val data = withContext(Dispatchers.IO) {
            val bufferDepth = try { transactionDao.countForLocalApi() } catch (_: Exception) { 0 }
            val syncState = try { syncStateDao.get() } catch (_: Exception) { null }
            val recentLogs = try { auditLogDao.getRecent(RECENT_LOG_LIMIT) } catch (_: Exception) { emptyList() }
            val siteInfo = try { siteDataDao.getSiteInfo() } catch (_: Exception) { null }
            val productCount = try { siteDataDao.countProducts() } catch (_: Exception) { 0 }
            val pumpCount = try { siteDataDao.countPumps() } catch (_: Exception) { 0 }
            val nozzleCount = try { siteDataDao.countNozzles() } catch (_: Exception) { 0 }
            val structuredEntries = try { fileLogger.getRecentDiagnosticEntries(STRUCTURED_LOG_LIMIT) } catch (_: Exception) { emptyList() }
            val logSizeBytes = try { fileLogger.totalLogSizeBytes() } catch (_: Exception) { 0L }

            val lastSyncUtc = syncState?.lastUploadAt
            val syncLagSeconds = if (lastSyncUtc != null) {
                try {
                    val lastSync = Instant.parse(lastSyncUtc)
                    ((System.currentTimeMillis() - lastSync.toEpochMilli()) / 1_000L).toInt()
                } catch (_: Exception) { null }
            } else null

            DiagnosticsSnapshot(
                connectivityState = connState,
                heartbeatAge = heartbeatAge,
                configVersion = configVersion,
                bufferDepth = bufferDepth,
                syncState = syncState,
                syncLagSeconds = syncLagSeconds,
                recentLogs = recentLogs,
                siteInfo = siteInfo,
                productCount = productCount,
                pumpCount = pumpCount,
                nozzleCount = nozzleCount,
                structuredEntries = structuredEntries,
                logSizeBytes = logSizeBytes,
                refreshedAt = Instant.now().toString().take(19),
            )
        }

        _snapshot.value = data
    }

    /** Returns log files for sharing (called from the Activity on Dispatchers.IO). */
    suspend fun getLogFiles(): List<File> = withContext(Dispatchers.IO) {
        fileLogger.getLogFiles()
    }

    companion object {
        private const val REFRESH_INTERVAL_MS = 5_000L
        private const val RECENT_LOG_LIMIT = 15
        private const val STRUCTURED_LOG_LIMIT = 30
    }
}
