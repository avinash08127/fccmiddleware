package com.fccmiddleware.agent.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.IBinder
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import timber.log.Timber

class FccAgentService : Service() {

    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    companion object {
        const val CHANNEL_ID = "fcc_agent_channel"
        const val NOTIFICATION_ID = 1
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        Timber.i("FCC Agent service created")
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val notification = buildNotification()
        startForeground(NOTIFICATION_ID, notification)
        Timber.i("FCC Agent service started in foreground")
        // TODO: launch polling, sync, and API server coroutines under serviceScope
        return START_STICKY
    }

    override fun onDestroy() {
        serviceScope.cancel()
        Timber.i("FCC Agent service destroyed")
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            "FCC Agent Service",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "Keeps the FCC Edge Agent running"
        }
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(channel)
    }

    private fun buildNotification(): Notification {
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("FCC Agent")
            .setContentText("Running")
            .setSmallIcon(android.R.drawable.ic_menu_manage)
            .build()
    }
}
