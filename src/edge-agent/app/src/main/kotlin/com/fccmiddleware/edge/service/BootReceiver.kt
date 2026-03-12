package com.fccmiddleware.edge.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import org.koin.core.component.KoinComponent
import org.koin.core.component.inject

class BootReceiver : BroadcastReceiver(), KoinComponent {

    private val tag = "BootReceiver"
    private val encryptedPrefs: EncryptedPrefsManager by inject()

    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED) return

        if (encryptedPrefs.isDecommissioned) {
            AppLogger.i(tag, "Boot completed — device is decommissioned, not starting service")
            return
        }

        if (!encryptedPrefs.isRegistered) {
            AppLogger.i(tag, "Boot completed — device not registered, not starting service")
            return
        }

        // M-03: Also check reprovisioning flag to avoid starting the service with
        // expired tokens. Without this, a partial flag write (reprovisioning=true
        // but isRegistered still true) would start the service with invalid tokens.
        if (encryptedPrefs.isReprovisioningRequired) {
            AppLogger.i(tag, "Boot completed — device requires re-provisioning, not starting service")
            return
        }

        AppLogger.i(tag, "Boot completed — device registered, starting EdgeAgentForegroundService")
        val serviceIntent = Intent(context, EdgeAgentForegroundService::class.java)
        context.startForegroundService(serviceIntent)
    }
}
