package com.fccmiddleware.edge.push

import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import com.fccmiddleware.edge.sync.PushHintKinds
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import org.koin.core.component.KoinComponent
import org.koin.core.component.inject

class EdgeFirebaseMessagingService : FirebaseMessagingService(), KoinComponent {

    companion object {
        private const val TAG = "EdgeFcmService"
        private const val HINT_THROTTLE_MS = 15_000L
    }

    private val encryptedPrefs: EncryptedPrefsManager by inject()

    override fun onNewToken(token: String) {
        if (token.isBlank()) {
            AppLogger.w(TAG, "Ignoring blank Firebase token rotation callback")
            return
        }

        if (!encryptedPrefs.isRegistered || encryptedPrefs.isDecommissioned || encryptedPrefs.isReprovisioningRequired) {
            AppLogger.i(TAG, "Firebase token rotated while device is not eligible for sync — no-op")
            return
        }

        encryptedPrefs.isAndroidInstallationSyncPending = true
        EdgeAgentForegroundService.requestInstallationTokenSync(
            context = this,
            source = "firebase_on_new_token",
            tokenOverride = token,
        )
    }

    override fun onMessageReceived(message: RemoteMessage) {
        if (!encryptedPrefs.isRegistered || encryptedPrefs.isDecommissioned || encryptedPrefs.isReprovisioningRequired) {
            AppLogger.i(TAG, "Ignoring Firebase hint while device is not eligible")
            return
        }

        val payload = message.data
        val kind = payload["kind"]?.trim().orEmpty()
        val targetDeviceId = payload["deviceId"]?.trim()
        val localDeviceId = encryptedPrefs.deviceId

        if (!targetDeviceId.isNullOrBlank() && !localDeviceId.isNullOrBlank() && targetDeviceId != localDeviceId) {
            AppLogger.w(TAG, "Ignoring Firebase hint for another device")
            return
        }

        when (kind) {
            PushHintKinds.COMMAND_PENDING -> handleCommandHint()
            PushHintKinds.CONFIG_CHANGED -> handleConfigHint()
            else -> AppLogger.w(TAG, "Ignoring unknown Firebase hint kind='$kind'")
        }
    }

    private fun handleCommandHint() {
        val now = System.currentTimeMillis()
        encryptedPrefs.pendingCommandHint = true
        if (now - encryptedPrefs.lastCommandHintAt < HINT_THROTTLE_MS) {
            AppLogger.d(TAG, "Command hint throttled")
            return
        }

        encryptedPrefs.lastCommandHintAt = now
        EdgeAgentForegroundService.requestImmediateCommandPoll(this, "firebase_hint")
    }

    private fun handleConfigHint() {
        val now = System.currentTimeMillis()
        encryptedPrefs.pendingConfigHint = true
        if (now - encryptedPrefs.lastConfigHintAt < HINT_THROTTLE_MS) {
            AppLogger.d(TAG, "Config hint throttled")
            return
        }

        encryptedPrefs.lastConfigHintAt = now
        EdgeAgentForegroundService.requestImmediateConfigPoll(this, "firebase_hint")
    }
}
