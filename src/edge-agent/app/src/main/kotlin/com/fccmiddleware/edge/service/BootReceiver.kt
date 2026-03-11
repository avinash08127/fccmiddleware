package com.fccmiddleware.edge.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import org.koin.core.component.KoinComponent
import org.koin.core.component.inject

class BootReceiver : BroadcastReceiver(), KoinComponent {

    private val tag = "BootReceiver"
    private val encryptedPrefs: EncryptedPrefsManager by inject()

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return

        if (encryptedPrefs.isDecommissioned) {
            Log.i(tag, "Boot completed — device is decommissioned, not starting service")
            return
        }

        if (!encryptedPrefs.isRegistered) {
            Log.i(tag, "Boot completed — device not registered, not starting service")
            return
        }

        Log.i(tag, "Boot completed — device registered, starting EdgeAgentForegroundService")
        val serviceIntent = Intent(context, EdgeAgentForegroundService::class.java)
        context.startForegroundService(serviceIntent)
    }
}
