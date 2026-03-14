package com.fccmiddleware.edge.ui.screens

import android.content.Intent
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.navigation.NavHostController
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import com.fccmiddleware.edge.ui.DiagnosticsViewModel
import com.fccmiddleware.edge.ui.navigation.Routes
import com.fccmiddleware.edge.ui.theme.DataRow
import com.fccmiddleware.edge.ui.theme.PumaButton
import com.fccmiddleware.edge.ui.theme.PumaGreen
import com.fccmiddleware.edge.ui.theme.PumaLogo
import com.fccmiddleware.edge.ui.theme.PumaOutlinedButton
import com.fccmiddleware.edge.ui.theme.SectionHeader
import com.fccmiddleware.edge.ui.theme.StatusGreen
import com.fccmiddleware.edge.ui.theme.StatusRed
import com.fccmiddleware.edge.ui.theme.StatusYellow
import com.fccmiddleware.edge.ui.theme.TextGray
import com.fccmiddleware.edge.ui.theme.TextLabel
import com.fccmiddleware.edge.ui.theme.TextPrimary
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.koin.androidx.compose.koinViewModel

private const val TAG = "DiagnosticsScreen"
private const val SHARED_LOG_ARCHIVE_PREFIX = "edge-agent-logs"

private val CRITICAL_EVENT_TYPES = setOf(
    "DB_CORRUPTION_DETECTED",
    "PREAUTH_EXPIRED",
    "CONNECTIVITY_TRANSITION",
)

@Composable
fun DiagnosticsScreen(navController: NavHostController) {
    val viewModel: DiagnosticsViewModel = koinViewModel()
    val snapshot by viewModel.snapshot.collectAsStateWithLifecycle()
    val context = LocalContext.current
    val scope = rememberCoroutineScope()

    var showLogsDialog by remember { mutableStateOf(false) }
    var showShareConfirm by remember { mutableStateOf(false) }
    var isShareInProgress by remember { mutableStateOf(false) }

    val shareLogsLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) {
        isShareInProgress = false
    }

    DisposableEffect(Unit) {
        cleanupSharedLogArchives(context)
        viewModel.startAutoRefresh()
        onDispose { viewModel.stopAutoRefresh() }
    }

    Column(modifier = Modifier.fillMaxSize()) {
        // Scrollable content
        Column(
            modifier = Modifier
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp, vertical = 8.dp),
        ) {
            PumaLogo(
                modifier = Modifier
                    .width(140.dp)
                    .align(Alignment.CenterHorizontally)
                    .padding(top = 8.dp, bottom = 16.dp),
            )

            val data = snapshot

            // Connectivity section
            SectionHeader("Connectivity")
            DataRow(
                "State:",
                data?.connectivityState?.name ?: "Loading...",
                connectivityColor(data?.connectivityState),
            )
            DataRow(
                "FCC Heartbeat:",
                if (data?.heartbeatAge != null) "${data.heartbeatAge}s ago" else "No probe yet",
                heartbeatColor(data?.heartbeatAge),
            )

            // Buffer section
            SectionHeader("Transaction Buffer")
            DataRow(
                "Buffer Depth:",
                data?.bufferDepth?.toString() ?: "...",
                bufferColor(data?.bufferDepth ?: 0),
            )

            // Sync section
            SectionHeader("Cloud Sync")
            DataRow(
                "Sync Lag:",
                if (data?.syncLagSeconds != null) "${data.syncLagSeconds}s" else "N/A",
                syncLagColor(data?.syncLagSeconds),
            )
            DataRow(
                "Last Sync:",
                data?.syncState?.lastUploadAt ?: "Never",
            )

            // Config section
            SectionHeader("Configuration")
            DataRow("Config Version:", data?.configVersion?.toString() ?: "None")
            DataRow(
                "Local Overrides:",
                if (data?.activeOverrides?.isNotEmpty() == true) data.activeOverrides.joinToString(", ")
                else "None",
                if (data?.activeOverrides?.isNotEmpty() == true) StatusYellow else StatusGreen,
            )

            // Site Data section
            SectionHeader("Site Data")
            if (data?.siteInfo != null) {
                val vendor = data.siteInfo.fccVendor ?: "Unknown"
                val model = data.siteInfo.fccModel ?: ""
                DataRow("FCC Type:", if (model.isNotEmpty()) "$vendor / $model" else vendor)
                DataRow("Products:", data.productCount.toString())
                DataRow("Pumps:", data.pumpCount.toString())
                DataRow("Nozzles:", data.nozzleCount.toString())
                DataRow("Last Synced:", data.siteInfo.syncedAt.take(19))
            } else {
                DataRow("FCC Type:", "No site data", TextGray)
                DataRow("Products:", "-")
                DataRow("Pumps:", "-")
                DataRow("Nozzles:", "-")
                DataRow("Last Synced:", "-")
            }

            // Recent audit log
            SectionHeader("Recent Activity")
            val entries = data?.recentLogs ?: emptyList()
            if (entries.isEmpty()) {
                Text(
                    text = "No recent audit entries",
                    fontSize = 12.sp,
                    color = TextGray,
                    modifier = Modifier.padding(vertical = 4.dp),
                )
            } else {
                entries.forEach { entry ->
                    val isCritical = entry.eventType in CRITICAL_EVENT_TYPES ||
                        entry.eventType.contains("ERROR") ||
                        entry.eventType.contains("FAIL")
                    Text(
                        text = "${entry.createdAt.take(19)} [${entry.eventType}] ${entry.message}",
                        fontSize = 11.sp,
                        color = if (isCritical) StatusRed else TextPrimary,
                        modifier = Modifier.padding(vertical = 2.dp),
                    )
                }
            }

            Spacer(modifier = Modifier.height(12.dp))
            Text(
                text = "Last refresh: ${data?.refreshedAt ?: ""}",
                fontSize = 11.sp,
                color = TextGray,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth(),
            )
        }

        // Fixed bottom bar
        HorizontalDivider(color = Color(0xFFE0E0E0))
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .background(Color.White)
                .padding(horizontal = 16.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            PumaButton(
                text = "Refresh",
                onClick = {
                    EdgeAgentForegroundService.requestImmediateConfigPoll(
                        context,
                        "diagnostics_manual_refresh",
                    )
                    Toast.makeText(context, "Config refresh requested", Toast.LENGTH_SHORT).show()
                },
                modifier = Modifier.weight(1f),
            )
            PumaButton(
                text = "Settings",
                onClick = {
                    navController.navigate(Routes.SETTINGS) {
                        popUpTo(Routes.SITE_OVERVIEW) { inclusive = false }
                        launchSingleTop = true
                    }
                },
                modifier = Modifier.weight(1f),
            )
            PumaOutlinedButton(
                text = "Share",
                onClick = { showShareConfirm = true },
                modifier = Modifier.weight(1f),
            )
        }
    }

    // File Logs dialog
    if (showLogsDialog) {
        val data = snapshot
        AlertDialog(
            onDismissRequest = { showLogsDialog = false },
            title = { Text("File Logs (WARN/ERROR)") },
            text = {
                Column(modifier = Modifier.verticalScroll(rememberScrollState())) {
                    DataRow("Log file size:", "${(data?.logSizeBytes ?: 0) / 1024} KB")
                    Spacer(modifier = Modifier.height(8.dp))
                    val logEntries = data?.structuredEntries ?: emptyList()
                    if (logEntries.isEmpty()) {
                        Text(
                            text = "No recent WARN/ERROR file log entries",
                            fontSize = 12.sp,
                            color = TextGray,
                        )
                    } else {
                        logEntries.forEach { entry ->
                            val isError = entry.contains("\"lvl\":\"ERROR\"") || entry.contains("\"lvl\":\"FATAL\"")
                            Text(
                                text = entry.take(200),
                                fontSize = 10.sp,
                                fontFamily = FontFamily.Monospace,
                                color = if (isError) StatusRed else StatusYellow,
                                modifier = Modifier.padding(vertical = 1.dp),
                            )
                        }
                    }
                }
            },
            confirmButton = {
                TextButton(onClick = { showLogsDialog = false }) { Text("Close") }
            },
        )
    }

    // Share confirmation dialog
    if (showShareConfirm) {
        AlertDialog(
            onDismissRequest = { showShareConfirm = false },
            title = { Text("Share Redacted Logs") },
            text = {
                Text(
                    "The exported archive redacts network identifiers and stack traces, " +
                        "but it still contains operational timing and error summaries. " +
                        "Only share it with authorized support personnel.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    showShareConfirm = false
                    if (!isShareInProgress) {
                        isShareInProgress = true
                        scope.launch {
                            var launchedIntent = false
                            try {
                                cleanupSharedLogArchives(context)
                                val archive = withContext(Dispatchers.IO) {
                                    viewModel.exportRedactedLogArchive(context.cacheDir)
                                }
                                if (archive == null) {
                                    Toast.makeText(context, "No log files to share", Toast.LENGTH_SHORT).show()
                                    return@launch
                                }
                                val uri = FileProvider.getUriForFile(
                                    context,
                                    "${context.packageName}.fileprovider",
                                    archive.file,
                                )
                                val shareIntent = Intent(Intent.ACTION_SEND).apply {
                                    type = "application/zip"
                                    putExtra(Intent.EXTRA_STREAM, uri)
                                    putExtra(Intent.EXTRA_SUBJECT, "FCC Edge Agent Logs (Redacted)")
                                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                                }
                                shareLogsLauncher.launch(Intent.createChooser(shareIntent, "Share Logs"))
                                launchedIntent = true
                                AppLogger.i(TAG, "Redacted log share initiated, ${archive.fileCount} files zipped")
                            } catch (e: Exception) {
                                AppLogger.e(TAG, "Failed to share logs", e)
                                Toast.makeText(context, "Failed to share logs", Toast.LENGTH_SHORT).show()
                            } finally {
                                if (!launchedIntent) {
                                    cleanupSharedLogArchives(context)
                                    isShareInProgress = false
                                }
                            }
                        }
                    }
                }) { Text("Share") }
            },
            dismissButton = {
                TextButton(onClick = { showShareConfirm = false }) { Text("Cancel") }
            },
        )
    }
}

private fun cleanupSharedLogArchives(context: android.content.Context) {
    context.cacheDir.listFiles()
        ?.filter { it.isFile && it.name.startsWith(SHARED_LOG_ARCHIVE_PREFIX) && it.extension == "zip" }
        ?.forEach { archive ->
            if (!archive.delete()) archive.deleteOnExit()
        }
}

private fun connectivityColor(state: ConnectivityState?): Color = when (state) {
    ConnectivityState.FULLY_ONLINE -> StatusGreen
    ConnectivityState.INTERNET_DOWN -> StatusYellow
    else -> StatusRed
}

private fun heartbeatColor(age: Int?): Color = when {
    age == null -> TextGray
    age <= 60 -> StatusGreen
    age <= 300 -> StatusYellow
    else -> StatusRed
}

private fun bufferColor(depth: Int): Color = when {
    depth == 0 -> StatusGreen
    depth < 100 -> StatusYellow
    else -> StatusRed
}

private fun syncLagColor(lag: Int?): Color = when {
    lag == null -> TextGray
    lag < 60 -> StatusGreen
    lag < 300 -> StatusYellow
    else -> StatusRed
}
