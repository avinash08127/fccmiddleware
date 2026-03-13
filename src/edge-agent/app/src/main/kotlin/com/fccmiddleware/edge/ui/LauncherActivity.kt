package com.fccmiddleware.edge.ui

import android.app.ForegroundServiceStartNotAllowedException
import android.content.Intent
import android.os.Build
import android.os.Bundle
import com.fccmiddleware.edge.logging.AppLogger
import androidx.appcompat.app.AppCompatActivity
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import org.koin.android.ext.android.inject

/**
 * LauncherActivity — thin routing activity that checks registration state on startup.
 *
 * Decision tree:
 *   1. If decommissioned → DecommissionedActivity
 *   2. If registered → start foreground service + DiagnosticsActivity
 *   3. If not registered → ProvisioningActivity
 *
 * Uses a transparent theme to prevent an empty window flash during routing.
 * This activity finishes immediately after routing.
 */
class LauncherActivity : AppCompatActivity() {

    companion object {
        private const val TAG = "LauncherActivity"
    }

    private val encryptedPrefs: EncryptedPrefsManager by inject()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val target = try {
            when {
                encryptedPrefs.isDecommissioned -> {
                    AppLogger.i(TAG, "Device is decommissioned — showing decommissioned screen")
                    Intent(this, DecommissionedActivity::class.java)
                }
                encryptedPrefs.isRegistered -> {
                    AppLogger.i(TAG, "Device is registered — starting service and showing diagnostics")
                    startForegroundServiceSafely()
                    Intent(this, DiagnosticsActivity::class.java)
                }
                else -> {
                    AppLogger.i(TAG, "Device not registered — showing provisioning screen")
                    Intent(this, ProvisioningActivity::class.java)
                }
            }
        } catch (e: Exception) {
            // EncryptedSharedPreferences can throw on Keystore corruption or first-boot race.
            // Fall back to provisioning so the user can re-register.
            AppLogger.e(TAG, "Failed to read registration state — routing to provisioning", e)
            Intent(this, ProvisioningActivity::class.java)
        }

        target.flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        startActivity(target)
        finish()
    }

    /**
     * Safely start the foreground service, handling Android 12+ background launch restrictions.
     * ForegroundServiceStartNotAllowedException is thrown when the app is not in a state that
     * allows starting foreground services (e.g., backgrounded launch without a visible activity).
     */
    private fun startForegroundServiceSafely() {
        try {
            startForegroundService(Intent(this, EdgeAgentForegroundService::class.java))
        } catch (e: Exception) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
                e is ForegroundServiceStartNotAllowedException
            ) {
                AppLogger.w(TAG, "Cannot start foreground service from background — service will start from DiagnosticsActivity")
            } else {
                AppLogger.e(TAG, "Failed to start foreground service", e)
            }
        }
    }
}
