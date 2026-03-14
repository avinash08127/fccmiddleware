package com.fccmiddleware.edge.ui

import android.content.Context
import android.content.Intent
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.config.LocalOverrideManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Extracts the shared credential-clearing and service-stopping logic used by
 * both SettingsScreen (manual reprovision) and DecommissionedScreen (re-provision).
 *
 * AF-004: Stop service before clearing credentials.
 * AF-013: Clear Room database before credentials (cross-site contamination).
 * AF-052: Clear local FCC overrides.
 */
object ReprovisionHelper {

    private const val TAG = "ReprovisionHelper"

    suspend fun execute(
        context: Context,
        bufferDatabase: BufferDatabase,
        keystoreManager: KeystoreManager,
        encryptedPrefs: EncryptedPrefsManager,
        localOverrideManager: LocalOverrideManager,
    ) = withContext(Dispatchers.IO) {
        AppLogger.w(TAG, "Reprovision initiated — clearing all local state")

        // 1. Stop the foreground service to prevent START_STICKY restart races (AF-004)
        context.stopService(Intent(context, EdgeAgentForegroundService::class.java))

        // 2. Clear Room database before credentials to prevent cross-site data contamination (AF-013)
        bufferDatabase.clearAllData()

        // 3. Clear Keystore keys and encrypted preferences
        keystoreManager.clearAll()
        encryptedPrefs.clearAll()

        // 4. Clear local FCC overrides (AF-052)
        localOverrideManager.clearAllOverrides()

        AppLogger.i(TAG, "Reprovision cleanup complete")
    }
}
