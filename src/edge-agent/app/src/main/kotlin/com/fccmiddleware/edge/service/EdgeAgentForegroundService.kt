package com.fccmiddleware.edge.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.IBinder
import android.util.Log
import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.IFccAdapterFactory
import com.fccmiddleware.edge.api.LocalApiServer
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.EdgeAgentConfigDto
import com.fccmiddleware.edge.config.requiresFccRuntime
import com.fccmiddleware.edge.config.toAgentFccConfig
import com.fccmiddleware.edge.config.toLocalApiServerConfig
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.preauth.PreAuthHandler
import com.fccmiddleware.edge.runtime.CadenceController
import com.fccmiddleware.edge.runtime.FccRuntimeState
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import com.fccmiddleware.edge.ui.ProvisioningActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
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
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

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
        Log.i(TAG, "EdgeAgentForegroundService created")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val notification = buildNotification()
        startForeground(NOTIFICATION_ID, notification)
        Log.i(TAG, "EdgeAgentForegroundService started in foreground")

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

            localApiServer.start()
            cadenceController.start()
            observeConfigForRuntimeUpdates()
        }

        // Monitor for re-provisioning requirement (refresh token expired).
        // Checks periodically and navigates to ProvisioningActivity if needed.
        serviceScope.launch {
            monitorReprovisioningState()
        }

        return START_STICKY
    }

    override fun onDestroy() {
        cadenceController.stop()
        connectivityManager.stop()
        localApiServer.stop()
        serviceScope.cancel()
        Log.i(TAG, "EdgeAgentForegroundService destroyed")
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
        Log.i(TAG, "Bootstrap runtime configured without site config")
    }

    private fun applyRuntimeConfig(siteConfig: EdgeAgentConfigDto, source: String): Boolean {
        val agentVersion = packageManager.getPackageInfo(packageName, 0).versionName ?: "1.0.0"

        cloudApiClient.updateBaseUrl(siteConfig.sync.cloudBaseUrl)
        encryptedPrefs.cloudBaseUrl = siteConfig.sync.cloudBaseUrl

        localApiServer.reconfigure(
            config = siteConfig.toLocalApiServerConfig(),
            deviceId = siteConfig.identity.deviceId,
            siteCode = siteConfig.identity.siteCode,
            agentVersion = agentVersion,
        )

        if (!siteConfig.requiresFccRuntime()) {
            clearFccRuntime()
            lastAppliedConfigVersion = siteConfig.configVersion
            Log.i(TAG, "Applied config v${siteConfig.configVersion} from $source without FCC runtime")
            return true
        }

        val agentFccConfig = try {
            siteConfig.toAgentFccConfig()
        } catch (e: Exception) {
            return failRuntimeReadiness(
                "Config v${siteConfig.configVersion} from $source cannot build AgentFccConfig: ${e.message}",
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
        cadenceController.updateFccAdapter(adapter)

        encryptedPrefs.fccHost = agentFccConfig.hostAddress
        encryptedPrefs.fccPort = agentFccConfig.port
        lastAppliedConfigVersion = siteConfig.configVersion

        Log.i(
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
                Log.w(TAG, "Re-provisioning required — stopping service and navigating to provisioning")
                cadenceController.stop()
                localApiServer.stop()
                navigateToProvisioning()
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

    private fun failRuntimeReadiness(reason: String): Boolean {
        Log.e(TAG, "Runtime readiness failure: $reason")
        if (encryptedPrefs.isRegistered) {
            stopSelf()
        }
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
            .setSmallIcon(android.R.drawable.ic_menu_manage)
            .setOngoing(true)
            .build()
    }
}
