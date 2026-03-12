package com.fccmiddleware.edge.ui

import android.content.Intent
import android.graphics.Typeface
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.FileProvider
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.dao.SiteDataDao
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.logging.StructuredFileLogger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.koin.android.ext.android.inject
import java.io.File
import java.io.FileOutputStream
import java.time.Instant
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * DiagnosticsActivity — supervisor/technician diagnostics screen.
 *
 * Shows: connectivity state, buffer depth, last sync timestamp,
 * FCC heartbeat status, config version, and recent audit log entries.
 *
 * Auto-refreshes every 5 seconds while visible.
 */
class DiagnosticsActivity : AppCompatActivity() {

    private val connectivityManager: ConnectivityManager by inject()
    private val siteDataDao: SiteDataDao by inject()
    private val transactionDao: TransactionBufferDao by inject()
    private val syncStateDao: SyncStateDao by inject()
    private val auditLogDao: AuditLogDao by inject()
    private val configManager: ConfigManager by inject()
    private val fileLogger: StructuredFileLogger by inject()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val handler = Handler(Looper.getMainLooper())
    private val refreshRunnable = Runnable { refreshData() }

    // UI references for dynamic updates
    private lateinit var connectivityValue: TextView
    private lateinit var fccHeartbeatValue: TextView
    private lateinit var bufferDepthValue: TextView
    private lateinit var syncLagValue: TextView
    private lateinit var lastSyncValue: TextView
    private lateinit var configVersionValue: TextView
    private lateinit var recentErrorsContainer: LinearLayout
    private lateinit var structuredLogsContainer: LinearLayout
    private lateinit var logFileSizeValue: TextView
    private lateinit var fccTypeValue: TextView
    private lateinit var productCountValue: TextView
    private lateinit var pumpCountValue: TextView
    private lateinit var nozzleCountValue: TextView
    private lateinit var siteLastSyncValue: TextView
    private lateinit var lastRefreshValue: TextView

    // Constants moved to bottom companion object

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())
        refreshData()
    }

    override fun onResume() {
        super.onResume()
        handler.postDelayed(refreshRunnable, REFRESH_INTERVAL_MS)
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(refreshRunnable)
    }

    override fun onDestroy() {
        super.onDestroy()
        scope.cancel()
    }

    private fun refreshData() {
        scope.launch {
            val connState = connectivityManager.state.value
            val heartbeatAge = connectivityManager.fccHeartbeatAgeSeconds()

            val bufferDepth = withContext(Dispatchers.IO) {
                try { transactionDao.countForLocalApi() } catch (_: Exception) { 0 }
            }

            val syncState = withContext(Dispatchers.IO) {
                try { syncStateDao.get() } catch (_: Exception) { null }
            }

            val recentLogs = withContext(Dispatchers.IO) {
                try { auditLogDao.getRecent(RECENT_LOG_LIMIT) } catch (_: Exception) { emptyList() }
            }

            val configVersion = configManager.currentConfigVersion

            val siteInfo = withContext(Dispatchers.IO) {
                try { siteDataDao.getSiteInfo() } catch (_: Exception) { null }
            }
            val productCount = withContext(Dispatchers.IO) {
                try { siteDataDao.getAllProducts().size } catch (_: Exception) { 0 }
            }
            val pumpCount = withContext(Dispatchers.IO) {
                try { siteDataDao.getAllPumps().size } catch (_: Exception) { 0 }
            }
            val nozzleCount = withContext(Dispatchers.IO) {
                try { siteDataDao.getAllNozzles().size } catch (_: Exception) { 0 }
            }

            val lastSyncUtc = syncState?.lastUploadAt

            val syncLagSeconds = if (lastSyncUtc != null) {
                try {
                    val lastSync = Instant.parse(lastSyncUtc)
                    ((System.currentTimeMillis() - lastSync.toEpochMilli()) / 1_000L).toInt()
                } catch (_: Exception) { null }
            } else null

            // Update UI on main thread
            connectivityValue.text = connState.name
            connectivityValue.setTextColor(
                when (connState) {
                    com.fccmiddleware.edge.adapter.common.ConnectivityState.FULLY_ONLINE -> COLOR_GREEN
                    com.fccmiddleware.edge.adapter.common.ConnectivityState.INTERNET_DOWN -> COLOR_YELLOW
                    else -> COLOR_RED
                }
            )

            fccHeartbeatValue.text = if (heartbeatAge != null) "${heartbeatAge}s ago" else "No probe yet"
            fccHeartbeatValue.setTextColor(
                when {
                    heartbeatAge == null -> COLOR_GRAY
                    heartbeatAge <= 60 -> COLOR_GREEN
                    heartbeatAge <= 300 -> COLOR_YELLOW
                    else -> COLOR_RED
                }
            )

            bufferDepthValue.text = bufferDepth.toString()
            bufferDepthValue.setTextColor(
                when {
                    bufferDepth == 0 -> COLOR_GREEN
                    bufferDepth < 100 -> COLOR_YELLOW
                    else -> COLOR_RED
                }
            )

            syncLagValue.text = if (syncLagSeconds != null) "${syncLagSeconds}s" else "N/A"
            syncLagValue.setTextColor(
                when {
                    syncLagSeconds == null -> COLOR_GRAY
                    syncLagSeconds < 60 -> COLOR_GREEN
                    syncLagSeconds < 300 -> COLOR_YELLOW
                    else -> COLOR_RED
                }
            )

            lastSyncValue.text = lastSyncUtc ?: "Never"
            configVersionValue.text = configVersion?.toString() ?: "None"

            // Site data
            if (siteInfo != null) {
                val vendor = siteInfo.fccVendor ?: "Unknown"
                val model = siteInfo.fccModel ?: ""
                fccTypeValue.text = if (model.isNotEmpty()) "$vendor / $model" else vendor
                fccTypeValue.setTextColor(COLOR_TEXT)
                productCountValue.text = productCount.toString()
                pumpCountValue.text = pumpCount.toString()
                nozzleCountValue.text = nozzleCount.toString()
                siteLastSyncValue.text = siteInfo.syncedAt.take(19)
            } else {
                fccTypeValue.text = "No site data"
                fccTypeValue.setTextColor(COLOR_GRAY)
                productCountValue.text = "-"
                pumpCountValue.text = "-"
                nozzleCountValue.text = "-"
                siteLastSyncValue.text = "-"
            }

            // Recent audit log entries
            recentErrorsContainer.removeAllViews()
            if (recentLogs.isEmpty()) {
                recentErrorsContainer.addView(makeSecondary("No recent audit entries"))
            } else {
                for (entry in recentLogs) {
                    val line = TextView(this@DiagnosticsActivity).apply {
                        text = "${entry.createdAt.take(19)} [${entry.eventType}] ${entry.message}"
                        textSize = 11f
                        setTextColor(
                            if (entry.eventType.contains("ERROR") ||
                                entry.eventType.contains("FAIL")) COLOR_RED else COLOR_TEXT
                        )
                        setPadding(0, dp(2), 0, dp(2))
                    }
                    recentErrorsContainer.addView(line)
                }
            }

            // Structured file logs (WARN/ERROR)
            val structuredEntries = withContext(Dispatchers.IO) {
                try { fileLogger.getRecentDiagnosticEntries(STRUCTURED_LOG_LIMIT) } catch (_: Exception) { emptyList() }
            }
            structuredLogsContainer.removeAllViews()
            if (structuredEntries.isEmpty()) {
                structuredLogsContainer.addView(makeSecondary("No recent WARN/ERROR file log entries"))
            } else {
                for (entry in structuredEntries) {
                    val line = TextView(this@DiagnosticsActivity).apply {
                        text = entry.take(200)
                        textSize = 10f
                        setTypeface(Typeface.MONOSPACE, Typeface.NORMAL)
                        setTextColor(if (entry.contains("\"lvl\":\"ERROR\"") || entry.contains("\"lvl\":\"FATAL\"")) COLOR_RED else COLOR_YELLOW)
                        setPadding(0, dp(1), 0, dp(1))
                    }
                    structuredLogsContainer.addView(line)
                }
            }

            logFileSizeValue.text = "${fileLogger.totalLogSizeBytes() / 1024} KB"
            lastRefreshValue.text = "Last refresh: ${Instant.now().toString().take(19)}"

            // Schedule next refresh
            handler.removeCallbacks(refreshRunnable)
            handler.postDelayed(refreshRunnable, REFRESH_INTERVAL_MS)
        }
    }

    // -------------------------------------------------------------------------
    // Layout building
    // -------------------------------------------------------------------------

    private fun buildLayout(): View {
        val padding = dp(16)

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding, padding, padding)
        }

        // Title
        root.addView(TextView(this).apply {
            text = "Edge Agent Diagnostics"
            textSize = 20f
            setTypeface(null, Typeface.BOLD)
            gravity = Gravity.CENTER
            setPadding(0, 0, 0, dp(16))
        })

        // Connectivity section
        root.addView(makeSectionHeader("Connectivity"))
        connectivityValue = makeValue("Loading...")
        root.addView(makeRow("State:", connectivityValue))

        fccHeartbeatValue = makeValue("Loading...")
        root.addView(makeRow("FCC Heartbeat:", fccHeartbeatValue))

        // Buffer section
        root.addView(makeSectionHeader("Transaction Buffer"))
        bufferDepthValue = makeValue("...")
        root.addView(makeRow("Buffer Depth:", bufferDepthValue))

        // Sync section
        root.addView(makeSectionHeader("Cloud Sync"))
        syncLagValue = makeValue("...")
        root.addView(makeRow("Sync Lag:", syncLagValue))

        lastSyncValue = makeValue("...")
        root.addView(makeRow("Last Sync:", lastSyncValue))

        // Config section
        root.addView(makeSectionHeader("Configuration"))
        configVersionValue = makeValue("...")
        root.addView(makeRow("Config Version:", configVersionValue))

        // Site Data section
        root.addView(makeSectionHeader("Site Data"))
        fccTypeValue = makeValue("...")
        root.addView(makeRow("FCC Type:", fccTypeValue))

        productCountValue = makeValue("...")
        root.addView(makeRow("Products:", productCountValue))

        pumpCountValue = makeValue("...")
        root.addView(makeRow("Pumps:", pumpCountValue))

        nozzleCountValue = makeValue("...")
        root.addView(makeRow("Nozzles:", nozzleCountValue))

        siteLastSyncValue = makeValue("...")
        root.addView(makeRow("Last Synced:", siteLastSyncValue))

        // Recent audit log
        root.addView(makeSectionHeader("Recent Activity"))
        recentErrorsContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        root.addView(recentErrorsContainer)

        // Structured file logs (WARN/ERROR)
        root.addView(makeSectionHeader("File Logs (WARN/ERROR)"))
        logFileSizeValue = makeValue("...")
        root.addView(makeRow("Log file size:", logFileSizeValue))
        structuredLogsContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        root.addView(structuredLogsContainer)

        // Action buttons
        root.addView(View(this).apply { minimumHeight = dp(12) })
        root.addView(Button(this).apply {
            text = "Settings"
            setOnClickListener {
                startActivity(Intent(this@DiagnosticsActivity, SettingsActivity::class.java))
            }
        })

        root.addView(View(this).apply { minimumHeight = dp(8) })
        root.addView(Button(this).apply {
            text = "Share Logs"
            setOnClickListener { shareLogs() }
        })

        // Last refresh timestamp
        root.addView(View(this).apply {
            minimumHeight = dp(16)
        })
        lastRefreshValue = TextView(this).apply {
            textSize = 11f
            setTextColor(COLOR_GRAY)
            gravity = Gravity.CENTER
        }
        root.addView(lastRefreshValue)

        val scrollView = ScrollView(this)
        scrollView.addView(root)
        return scrollView
    }

    private fun makeSectionHeader(title: String): TextView {
        return TextView(this).apply {
            text = title
            textSize = 15f
            setTypeface(null, Typeface.BOLD)
            setPadding(0, dp(12), 0, dp(4))
            setTextColor(COLOR_TEXT)
        }
    }

    private fun makeRow(label: String, valueView: TextView): LinearLayout {
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, dp(2), 0, dp(2))
            addView(TextView(this@DiagnosticsActivity).apply {
                text = label
                textSize = 14f
                setTextColor(COLOR_LABEL)
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            })
            addView(valueView.apply {
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
                gravity = Gravity.END
            })
        }
    }

    private fun makeValue(initial: String): TextView {
        return TextView(this).apply {
            text = initial
            textSize = 14f
            setTypeface(null, Typeface.BOLD)
            setTextColor(COLOR_TEXT)
        }
    }

    private fun makeSecondary(text: String): TextView {
        return TextView(this).apply {
            this.text = text
            textSize = 12f
            setTextColor(COLOR_GRAY)
            setPadding(0, dp(4), 0, dp(4))
        }
    }

    // ── Phase 2B: Share Logs ────────────────────────────────────────────────

    private fun shareLogs() {
        scope.launch {
            try {
                val logFiles = withContext(Dispatchers.IO) { fileLogger.getLogFiles() }
                if (logFiles.isEmpty()) {
                    AppLogger.i(TAG, "No log files to share")
                    return@launch
                }

                val zipFile = withContext(Dispatchers.IO) {
                    val zip = File(cacheDir, "edge-agent-logs.zip")
                    ZipOutputStream(FileOutputStream(zip)).use { zos ->
                        for (file in logFiles) {
                            zos.putNextEntry(ZipEntry(file.name))
                            file.inputStream().use { it.copyTo(zos) }
                            zos.closeEntry()
                        }
                    }
                    zip
                }

                val uri = FileProvider.getUriForFile(
                    this@DiagnosticsActivity,
                    "${packageName}.fileprovider",
                    zipFile,
                )

                val shareIntent = Intent(Intent.ACTION_SEND).apply {
                    type = "application/zip"
                    putExtra(Intent.EXTRA_STREAM, uri)
                    putExtra(Intent.EXTRA_SUBJECT, "FCC Edge Agent Logs")
                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                }
                startActivity(Intent.createChooser(shareIntent, "Share Logs"))
                AppLogger.i(TAG, "Log share initiated, ${logFiles.size} files zipped")
            } catch (e: Exception) {
                AppLogger.e(TAG, "Failed to share logs", e)
            }
        }
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        private const val TAG = "DiagnosticsActivity"
        private const val REFRESH_INTERVAL_MS = 5_000L
        private const val RECENT_LOG_LIMIT = 15
        private const val STRUCTURED_LOG_LIMIT = 30
        private const val COLOR_GREEN = 0xFF2E7D32.toInt()
        private const val COLOR_YELLOW = 0xFFF9A825.toInt()
        private const val COLOR_RED = 0xFFC62828.toInt()
        private const val COLOR_GRAY = 0xFF9E9E9E.toInt()
        private const val COLOR_TEXT = 0xFF212121.toInt()
        private const val COLOR_LABEL = 0xFF616161.toInt()
    }
}
