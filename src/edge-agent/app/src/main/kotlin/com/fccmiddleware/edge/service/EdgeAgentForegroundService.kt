package com.fccmiddleware.edge.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.IBinder
import com.fccmiddleware.edge.logging.AppLogger
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
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import com.fccmiddleware.edge.websocket.OdooWebSocketServer
import com.fccmiddleware.edge.R
import com.fccmiddleware.edge.ui.DecommissionedActivity
import com.fccmiddleware.edge.ui.ProvisioningActivity
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
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

    // SupervisorJob ensures one failing child does not cancel siblings.
    // CoroutineExceptionHandler logs uncaught exceptions to prevent silent service crashes (T-008).
    private val serviceScope = CoroutineScope(
        SupervisorJob() + Dispatchers.IO + CoroutineExceptionHandler { _, throwable ->
            AppLogger.e(TAG, "Uncaught exception in service coroutine: ${throwable.message}", throwable)
        }
    )

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
    private val fileLogger: StructuredFileLogger by inject()
    private val localOverrideManager: LocalOverrideManager by inject()
    private val networkBinder: NetworkBinder by inject()
    private val odooWebSocketServer: OdooWebSocketServer by inject()

    @Volatile
    private var lastAppliedConfigVersion: Int? = null

    companion object {
        const val CHANNEL_ID = "fcc_edge_agent_channel"
        const val NOTIFICATION_ID = 1
        private const val TAG = "EdgeAgentFgService"
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

        // Skip re-initialisation if already running (T-004).
        if (!serviceStarted.compareAndSet(false, true)) {
            AppLogger.i(TAG, "onStartCommand: service already initialised — skipping duplicate setup")
            return START_STICKY
        }

        // Start network binder first so WiFi/mobile state flows are populated
        // before connectivity probes begin.
        networkBinder.start()

        // Start connectivity probes immediately (initializes in FULLY_OFFLINE per spec)
        connectivityManager.start()
        serviceScope.launch {
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
            cadenceController.start()
            observeConfigForRuntimeUpdates()
        }

        // Monitor for re-provisioning requirement (refresh token expired).
        // Checks periodically and navigates to ProvisioningActivity if needed.
        serviceScope.launch {
            monitorReprovisioningState()
        }

        // H-03: Monitor for decommissioned state so the running service stops
        // promptly instead of waiting for a full app relaunch.
        serviceScope.launch {
            monitorDecommissionedState()
        }

        return START_STICKY
    }

    override fun onDestroy() {
        serviceStarted.set(false)
        cadenceController.stop()
        connectivityManager.stop()
        networkBinder.stop()
        localApiServer.stop()
        odooWebSocketServer.stop()
        serviceScope.cancel()
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

        encryptedPrefs.fccHost = agentFccConfig.hostAddress
        encryptedPrefs.fccPort = agentFccConfig.port
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
     * Periodically checks if the device requires re-provisioning (refresh token expired).
     * When detected, stops all background work and navigates to ProvisioningActivity
     * so the user can re-provision with a new bootstrap token.
     */
    private suspend fun monitorReprovisioningState() {
        while (true) {
            delay(10_000L) // Check every 10 seconds
            if (tokenProvider.isReprovisioningRequired()) {
                AppLogger.w(TAG, "Re-provisioning required — stopping service and navigating to provisioning")
                cadenceController.stop()
                localApiServer.stop()
                navigateToProvisioning()
                stopSelf()
                return
            }
        }
    }

    /**
     * H-03: Periodically checks if the device has been decommissioned.
     * When detected, stops all background work and navigates to DecommissionedActivity
     * so the user sees the decommission screen immediately, without needing a full app relaunch.
     */
    private suspend fun monitorDecommissionedState() {
        while (true) {
            delay(10_000L) // Check every 10 seconds
            if (tokenProvider.isDecommissioned()) {
                AppLogger.w(TAG, "Device decommissioned — stopping service and navigating to decommissioned screen")
                cadenceController.stop()
                connectivityManager.stop()
                localApiServer.stop()
                navigateToDecommissioned()
                stopSelf()
                return
            }
        }
    }

    /**
     * Navigates to ProvisioningActivity with CLEAR_TASK so the provisioning
     * wizard becomes the only activity in the task stack.
     */
    private fun navigateToProvisioning() {
        val intent = Intent(this, ProvisioningActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        }
        startActivity(intent)
    }

    private fun navigateToDecommissioned() {
        val intent = Intent(this, DecommissionedActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        }
        startActivity(intent)
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
