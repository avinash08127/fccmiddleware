package com.fccmiddleware.edge.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

class BootReceiver : BroadcastReceiver() {

    private val tag = "BootReceiver"

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_BOOT_COMPLETED) {
            Log.i(tag, "Boot completed — starting EdgeAgentForegroundService")
            val serviceIntent = Intent(context, EdgeAgentForegroundService::class.java)
            context.startForegroundService(serviceIntent)
        }
    }
}
