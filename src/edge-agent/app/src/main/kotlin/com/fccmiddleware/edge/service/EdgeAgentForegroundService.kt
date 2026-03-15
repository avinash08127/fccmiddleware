package com.fccmiddleware.edge.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.app.Service
import android.content.Intent
import android.os.IBinder
import androidx.core.content.ContextCompat
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.buffer.IntegrityChecker
import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.IFccAdapterFactory
import com.fccmiddleware.edge.api.LocalApiServer
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.EdgeAgentConfigDto
import com.fccmiddleware.edge.config.LocalOverrideManager
import com.fccmiddleware.edge.config.requiresFccRuntime
import com.fccmiddleware.edge.config.toAgentFccConfig
import com.fccmiddleware.edge.config.toLocalApiServerConfig
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.connectivity.NetworkBinder
import com.fccmiddleware.edge.logging.LogLevel
import com.fccmiddleware.edge.logging.StructuredFileLogger
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.preauth.PreAuthHandler
import com.fccmiddleware.edge.runtime.CadenceController
import com.fccmiddleware.edge.runtime.FccRuntimeState
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.sync.AgentCommandExecutor
import com.fccmiddleware.edge.sync.AndroidInstallationSyncManager
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import com.fccmiddleware.edge.peer.PeerApiServer
import com.fccmiddleware.edge.peer.PeerCoordinator
import com.fccmiddleware.edge.websocket.OdooWebSocketServer
import com.fccmiddleware.edge.R
import com.fccmiddleware.edge.ui.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.util.concurrent.atomic.AtomicBoolean
import org.koin.android.ext.android.inject

/**
 * EdgeAgentForegroundService — thin always-on foreground service.
 *
 * Resident scope is intentionally limited to:
 *   - Local Ktor API server (port 8585)
 *   - Pre-auth relay path (LAN-only, top-latency path)
 *   - Connectivity manager + cadence controller
 *   - FCC polling orchestration
 *   - Replay triggers
 *
 * Non-critical work (telemetry, config refresh, diagnostics refresh, cleanup)
 * is coalesced and scheduled opportunistically from the cadence controller,
 * never left permanently hot as independent loops.
 */
class EdgeAgentForegroundService : Service() {

    // AT-003: Inject the single Koin-managed CoroutineScope instead of creating a
    // separate appScope. This eliminates lifecycle divergence — all coroutines
    // (service monitors, workers, handlers, logger) share one scope and are cancelled
    // together in onDestroy().
    private val appScope: CoroutineScope by inject()

    // Guard against re-entrant onStartCommand (T-004): START_STICKY may deliver
    // multiple calls without an intervening onDestroy (e.g., ProvisioningActivity
    // also calls startForegroundService). Only the first call runs full initialisation.
    private val serviceStarted = AtomicBoolean(false)

    private val localApiServer: LocalApiServer by inject()
    private val connectivityManager: ConnectivityManager by inject()
    private val cadenceController: CadenceController by inject()
    private val configManager: ConfigManager by inject()
    private val fccAdapterFactory: IFccAdapterFactory by inject()
    private val cloudApiClient: CloudApiClient by inject()
    private val encryptedPrefs: EncryptedPrefsManager by inject()
    private val fccRuntimeState: FccRuntimeState by inject()
    private val ingestionOrchestrator: IngestionOrchestrator by inject()
    private val preAuthHandler: PreAuthHandler by inject()
    private val tokenProvider: DeviceTokenProvider by inject()
    private val androidInstallationSyncManager: AndroidInstallationSyncManager by inject()
    private val agentCommandExecutor: AgentCommandExecutor by inject()
    private val fileLogger: StructuredFileLogger by inject()
    private val localOverrideManager: LocalOverrideManager by inject()
    private val networkBinder: NetworkBinder by inject()
    private val odooWebSocketServer: OdooWebSocketServer by inject()
    private val peerApiServer: PeerApiServer by inject()
    private val peerCoordinator: PeerCoordinator by inject()
    private val lanPeerAnnouncer: com.fccmiddleware.edge.peer.LanPeerAnnouncer by inject()
    private val lanPeerListener: com.fccmiddleware.edge.peer.LanPeerListener by inject()
    private val integrityChecker: IntegrityChecker by inject()

    @Volatile
    private var lastAppliedConfigVersion: Int? = null

    companion object {
        const val CHANNEL_ID = "fcc_edge_agent_channel"
        const val NOTIFICATION_ID = 1
        private const val TAG = "EdgeAgentFgService"
        /** AF-015: Max consecutive failures before a lifecycle monitor gives up. */
        private const val MAX_CONSECUTIVE_MONITOR_FAILURES = 10
        private const val ACTION_IMMEDIATE_COMMAND_POLL = "com.fccmiddleware.edge.action.IMMEDIATE_COMMAND_POLL"
        private const val ACTION_IMMEDIATE_CONFIG_POLL = "com.fccmiddleware.edge.action.IMMEDIATE_CONFIG_POLL"
        private const val ACTION_SYNC_ANDROID_INSTALLATION = "com.fccmiddleware.edge.action.SYNC_ANDROID_INSTALLATION"
        private const val EXTRA_SOURCE = "extra_source"
        private const val EXTRA_TOKEN_OVERRIDE = "extra_token_override"

        fun requestImmediateCommandPoll(context: Context, source: String) {
            startForegroundServiceForAction(
                context = context,
                action = ACTION_IMMEDIATE_COMMAND_POLL,
                source = source,
            )
        }

        fun requestImmediateConfigPoll(context: Context, source: String) {
            startForegroundServiceForAction(
                context = context,
                action = ACTION_IMMEDIATE_CONFIG_POLL,
                source = source,
            )
        }

        fun requestInstallationTokenSync(
            context: Context,
            source: String,
            tokenOverride: String? = null,
        ) {
            startForegroundServiceForAction(
                context = context,
                action = ACTION_SYNC_ANDROID_INSTALLATION,
                source = source,
                tokenOverride = tokenOverride,
            )
        }

        private fun startForegroundServiceForAction(
            context: Context,
            action: String,
            source: String,
            tokenOverride: String? = null,
        ) {
            val intent = Intent(context, EdgeAgentForegroundService::class.java).apply {
                this.action = action
                putExtra(EXTRA_SOURCE, source)
                if (!tokenOverride.isNullOrBlank()) {
                    putExtra(EXTRA_TOKEN_OVERRIDE, tokenOverride)
                }
            }

            runCatching { ContextCompat.startForegroundService(context, intent) }
                .onFailure { e ->
                    AppLogger.e(TAG, "Failed to dispatch foreground-service action $action", e as? Exception)
                }
        }
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        AppLogger.i(TAG, "EdgeAgentForegroundService created")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val notification = buildNotification()
        startForeground(NOTIFICATION_ID, notification)
        AppLogger.i(TAG, "EdgeAgentForegroundService started in foreground")

        if (agentCommandExecutor.finalizeAckedResetIfNeeded("service_startup")) {
            AppLogger.w(TAG, "Pending reset was finalized during service startup")
            stopSelf()
            return START_NOT_STICKY
        }

        // Skip re-initialisation if already running (T-004), but still handle action intents.
        if (!serviceStarted.compareAndSet(false, true)) {
            handleActionIntent(intent)
            AppLogger.i(TAG, "onStartCommand: service already initialised — handled action only")
            return START_STICKY
        }

        // Start network binder first so WiFi/mobile state flows are populated
        // before connectivity probes begin.
        networkBinder.start()

        // Start connectivity probes immediately (initializes in FULLY_OFFLINE per spec)
        connectivityManager.start()
        appScope.launch {
            // AF-038: Run database integrity check before any DB access.
            // If corruption is detected and recovered, stop the service —
            // START_STICKY will restart it with a fresh database.
            val integrityResult = integrityChecker.runCheck()
            if (integrityResult is IntegrityChecker.IntegrityCheckResult.Recovered) {
                AppLogger.w(TAG, "Database recovered from corruption (backup: ${integrityResult.backupPath}), restarting service")
                stopSelf()
                return@launch
            }

            configManager.loadFromLocal()
            val bootConfig = configManager.config.value
            if (bootConfig != null) {
                if (!applyRuntimeConfig(bootConfig, "startup")) {
                    return@launch
                }
            } else {
                configureBootstrapRuntime()
            }

            // Wire the reconnect callback so settings changes rebuild the adapter with overrides
            cadenceController.onFccReconnectRequested = {
                val cfg = configManager.config.value
                if (cfg != null) {
                    applyRuntimeConfig(cfg, "settings-override-reconnect")
                } else {
                    AppLogger.w(TAG, "FCC reconnect requested but no config available")
                }
            }

            localApiServer.start()
            odooWebSocketServer.start()

            // HA: Initialize peer coordinator and start peer API server if HA is enabled
            val bootHaConfig = configManager.config.value?.siteHa
            if (bootHaConfig != null && bootHaConfig.enabled) {
                peerCoordinator.initializeFromConfig()
                peerApiServer.start()
                // P2-12: Broadcast UDP peer announcement on startup so LAN peers discover us immediately
                lanPeerAnnouncer.broadcast()
                // P2-13: Start listening for UDP peer announcements from other agents
                lanPeerListener.onNewPeerDiscovered = {
                    cadenceController.triggerImmediateConfigPoll("lan_peer_discovered")
                }
                lanPeerListener.start(appScope)
                AppLogger.i(TAG, "HA enabled: peer API server started, coordinator initialized, LAN listener active")
            }

            cadenceController.start()
            observeConfigForRuntimeUpdates()
            androidInstallationSyncManager.syncCurrentInstallation("app_startup")
            handlePendingAgentControlSignals("startup")
            handleActionIntent(intent)
        }

        // Monitor for re-provisioning requirement (refresh token expired).
        // Checks periodically and navigates to ProvisioningActivity if needed.
        appScope.launch {
            monitorReprovisioningState()
        }

        // H-03: Monitor for decommissioned state so the running service stops
        // promptly instead of waiting for a full app relaunch.
        appScope.launch {
            monitorDecommissionedState()
        }

        return START_STICKY
    }

    override fun onDestroy() {
        serviceStarted.set(false)
        cadenceController.stop()
        // AT-006/AT-018: Synchronously close the FCC adapter to release TCP connections,
        // embedded Ktor servers (RadixPushListener, AdvatecWebhookListener), and
        // heartbeat managers before cancelling the app scope. cadenceController.stop()
        // launches a disconnect coroutine, but appScope.cancel() below would cancel
        // it before completion. FccRuntimeState.clear() calls IFccAdapter.close()
        // which is synchronous — DomsJplAdapter cancels its scope, RadixAdapter stops its
        // push listener, AdvatecAdapter stops its webhook listener, PetroniteAdapter
        // closes its HttpClient.
        fccRuntimeState.clear()
        connectivityManager.stop()
        networkBinder.stop()
        localApiServer.stop()
        peerApiServer.stop()
        lanPeerListener.stop()
        odooWebSocketServer.stop()
        fileLogger.close()
        // LR-004: Use cancelChildren() instead of cancel() so the Koin singleton scope
        // remains usable if the service is restarted via START_STICKY after decommission/
        // re-provision paths. cancel() permanently kills the scope; cancelChildren()
        // cancels all running coroutines but allows new launches after restart.
        appScope.coroutineContext[Job]?.cancelChildren()
        AppLogger.i(TAG, "EdgeAgentForegroundService destroyed")
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private suspend fun observeConfigForRuntimeUpdates() {
        configManager.config.collect { cfg ->
            if (cfg != null && cfg.configVersion != lastAppliedConfigVersion) {
                applyRuntimeConfig(cfg, "config-update")
            }
        }
    }

    private fun configureBootstrapRuntime() {
        clearFccRuntime()
        localApiServer.reconfigure(
            config = LocalApiServer.LocalApiServerConfig(),
            deviceId = encryptedPrefs.deviceId ?: "00000000-0000-0000-0000-000000000000",
            siteCode = encryptedPrefs.siteCode ?: "UNPROVISIONED",
            agentVersion = packageManager.getPackageInfo(packageName, 0).versionName ?: "1.0.0",
        )
        AppLogger.i(TAG, "Bootstrap runtime configured without site config")
    }

    private fun applyRuntimeConfig(siteConfig: EdgeAgentConfigDto, source: String): Boolean {
        val agentVersion = packageManager.getPackageInfo(packageName, 0).versionName ?: "1.0.0"

        // Phase 4: Wire log level from config to file logger
        val newLogLevel = LogLevel.fromString(siteConfig.telemetry.logLevel)
        if (fileLogger.minLevel != newLogLevel) {
            AppLogger.i(TAG, "Log level changed: ${fileLogger.minLevel} -> $newLogLevel (from config v${siteConfig.configVersion})")
            fileLogger.updateLogLevel(newLogLevel)
        }

        cloudApiClient.updateBaseUrl(siteConfig.sync.cloudBaseUrl)
        encryptedPrefs.cloudBaseUrl = siteConfig.sync.cloudBaseUrl

        localApiServer.reconfigure(
            config = siteConfig.toLocalApiServerConfig(),
            deviceId = siteConfig.identity.deviceId,
            siteCode = siteConfig.identity.siteCode,
            agentVersion = agentVersion,
        )

        // WebSocket server: reconfigure from site config
        odooWebSocketServer.wireSiteCode(siteConfig.identity.siteCode)
        odooWebSocketServer.reconfigure(siteConfig.websocket)

        if (!siteConfig.requiresFccRuntime()) {
            clearFccRuntime()
            lastAppliedConfigVersion = siteConfig.configVersion
            AppLogger.i(TAG, "Applied config v${siteConfig.configVersion} from $source without FCC runtime")
            return true
        }

        val agentFccConfig = try {
            siteConfig.toAgentFccConfig(overrideManager = localOverrideManager)
        } catch (e: Exception) {
            return failRuntimeReadiness(
                "Config v${siteConfig.configVersion} from $source cannot build AgentFccConfig: ${e.message}",
            )
        }

        // Log when local overrides are active so technicians can verify their changes
        if (localOverrideManager.hasAnyOverrides()) {
            AppLogger.i(
                TAG,
                "FCC config override active: host=${agentFccConfig.hostAddress}, port=${agentFccConfig.port} " +
                    "(cloud default: host=${siteConfig.fcc.hostAddress}, port=${siteConfig.fcc.port})",
            )
        }

        val adapter = try {
            fccAdapterFactory.resolve(agentFccConfig.fccVendor, agentFccConfig)
        } catch (e: Exception) {
            return failRuntimeReadiness(
                "Config v${siteConfig.configVersion} from $source cannot resolve FCC adapter: ${e.message}",
            )
        }

        fccRuntimeState.wire(adapter, agentFccConfig)
        ingestionOrchestrator.wireRuntime(adapter, agentFccConfig)
        preAuthHandler.wireFccAdapter(adapter)
        localApiServer.wireFccAdapter(adapter)
        odooWebSocketServer.wireFccAdapter(adapter)
        cadenceController.updateFccAdapter(adapter)

        // AP-030: Batch FCC host+port into single encrypted prefs write.
        encryptedPrefs.updateFccConnection(host = agentFccConfig.hostAddress, port = agentFccConfig.port)
        lastAppliedConfigVersion = siteConfig.configVersion

        AppLogger.i(
            TAG,
            "Applied config v${siteConfig.configVersion} from $source with FCC runtime " +
                "vendor=${agentFccConfig.fccVendor} host=${agentFccConfig.hostAddress}:${agentFccConfig.port}",
        )
        return true
    }

    private fun clearFccRuntime() {
        fccRuntimeState.clear()
        ingestionOrchestrator.wireRuntime(adapter = null, config = null)
        preAuthHandler.wireFccAdapter(null)
        localApiServer.wireFccAdapter(null)
        odooWebSocketServer.wireFccAdapter(null)
        cadenceController.updateFccAdapter(null)
    }

    /**
     * AF-015: Periodically checks if the device requires re-provisioning (refresh token expired).
     * Wrapped in try/catch so transient exceptions (e.g., Keystore corruption) do not
     * permanently kill the monitor. Stops after MAX_CONSECUTIVE_MONITOR_FAILURES consecutive
     * failures to prevent infinite crash loops.
     */
    private suspend fun monitorReprovisioningState() {
        var consecutiveFailures = 0
        while (true) {
            delay(10_000L) // Check every 10 seconds
            try {
                if (tokenProvider.isReprovisioningRequired()) {
                    AppLogger.w(TAG, "Re-provisioning required — stopping service and navigating to provisioning")
                    cadenceController.stop()
                    localApiServer.stop()
                    navigateToProvisioning()
                    stopSelf()
                    return
                }
                consecutiveFailures = 0
            } catch (e: Exception) {
                // LR-010: Rethrow CancellationException to allow proper coroutine cancellation.
                // Without this, scope.cancel() in onDestroy would be swallowed, causing 10
                // spurious error log entries before the loop exits via the failure guard.
                if (e is kotlinx.coroutines.CancellationException) throw e
                consecutiveFailures++
                AppLogger.e(TAG, "monitorReprovisioningState error ($consecutiveFailures/$MAX_CONSECUTIVE_MONITOR_FAILURES): ${e.message}", e)
                if (consecutiveFailures >= MAX_CONSECUTIVE_MONITOR_FAILURES) {
                    AppLogger.e(TAG, "monitorReprovisioningState exceeded $MAX_CONSECUTIVE_MONITOR_FAILURES consecutive failures — stopping monitor")
                    return
                }
            }
        }
    }

    /**
     * AF-015 / H-03: Periodically checks if the device has been decommissioned.
     * Same resilience pattern as monitorReprovisioningState.
     */
    private suspend fun monitorDecommissionedState() {
        var consecutiveFailures = 0
        while (true) {
            delay(10_000L) // Check every 10 seconds
            try {
                if (tokenProvider.isDecommissioned()) {
                    AppLogger.w(TAG, "Device decommissioned — stopping service and navigating to decommissioned screen")
                    cadenceController.stop()
                    connectivityManager.stop()
                    localApiServer.stop()
                    navigateToDecommissioned()
                    stopSelf()
                    return
                }
                consecutiveFailures = 0
            } catch (e: Exception) {
                // LR-010: Rethrow CancellationException to allow proper coroutine cancellation.
                if (e is kotlinx.coroutines.CancellationException) throw e
                consecutiveFailures++
                AppLogger.e(TAG, "monitorDecommissionedState error ($consecutiveFailures/$MAX_CONSECUTIVE_MONITOR_FAILURES): ${e.message}", e)
                if (consecutiveFailures >= MAX_CONSECUTIVE_MONITOR_FAILURES) {
                    AppLogger.e(TAG, "monitorDecommissionedState exceeded $MAX_CONSECUTIVE_MONITOR_FAILURES consecutive failures — stopping monitor")
                    return
                }
            }
        }
    }

    /**
     * AF-014: Navigates to ProvisioningActivity with CLEAR_TASK and an extra
     * indicating why re-provisioning is needed, so the UI can show context.
     */
    private fun navigateToProvisioning() {
        val intent = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        }
        startActivity(intent)
    }

    private fun navigateToDecommissioned() {
        val intent = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        }
        startActivity(intent)
    }

    private fun handlePendingAgentControlSignals(source: String) {
        if (encryptedPrefs.pendingCommandHint) {
            cadenceController.triggerImmediateCommandPoll("$source:pending_command_hint")
        }
        if (encryptedPrefs.pendingConfigHint) {
            cadenceController.triggerImmediateConfigPoll("$source:pending_config_hint")
        }
    }

    private fun handleActionIntent(intent: Intent?) {
        val action = intent?.action ?: return
        val source = intent.getStringExtra(EXTRA_SOURCE) ?: "unknown"

        when (action) {
            ACTION_IMMEDIATE_COMMAND_POLL -> {
                encryptedPrefs.pendingCommandHint = true
                cadenceController.triggerImmediateCommandPoll(source)
            }

            ACTION_IMMEDIATE_CONFIG_POLL -> {
                encryptedPrefs.pendingConfigHint = true
                cadenceController.triggerImmediateConfigPoll(source)
            }

            ACTION_SYNC_ANDROID_INSTALLATION -> {
                appScope.launch {
                    val tokenOverride = intent.getStringExtra(EXTRA_TOKEN_OVERRIDE)
                    androidInstallationSyncManager.syncCurrentInstallation(
                        reason = source,
                        tokenOverride = tokenOverride,
                    )
                }
            }
        }
    }

    /**
     * H-05: Instead of calling stopSelf() (which triggers an infinite restart loop
     * due to START_STICKY), fall back to degraded mode: clear the FCC runtime but
     * keep the service alive so config polling can deliver a corrected config push.
     */
    private fun failRuntimeReadiness(reason: String): Boolean {
        AppLogger.e(TAG, "Runtime readiness failure — entering degraded mode: $reason")
        clearFccRuntime()
        return false
    }

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "FCC Edge Agent",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "Keeps the FCC Edge Agent running persistently"
        }
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(channel)
    }

    private fun buildNotification(): Notification {
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("FCC Edge Agent Running")
            .setContentText("Monitoring forecourt and syncing transactions")
            .setSmallIcon(R.drawable.ic_notification)
            .setOngoing(true)
            .build()
    }
}
