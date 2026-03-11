package com.fccmiddleware.edge.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.IBinder
import android.util.Log
import com.fccmiddleware.edge.api.LocalApiServer
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.runtime.CadenceController
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
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

        // Start local API — independent of connectivity, always serves requests
        localApiServer.start()

        // Start cadence controller — coordinates all recurring resident work
        cadenceController.start()

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
