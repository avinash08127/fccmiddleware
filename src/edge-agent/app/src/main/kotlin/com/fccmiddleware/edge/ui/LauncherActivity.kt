package com.fccmiddleware.edge.ui

import android.content.Intent
import android.os.Bundle
import android.util.Log
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
 * This activity finishes immediately after routing.
 */
class LauncherActivity : AppCompatActivity() {

    companion object {
        private const val TAG = "LauncherActivity"
    }

    private val encryptedPrefs: EncryptedPrefsManager by inject()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val target = when {
            encryptedPrefs.isDecommissioned -> {
                Log.i(TAG, "Device is decommissioned — showing decommissioned screen")
                Intent(this, DecommissionedActivity::class.java)
            }
            encryptedPrefs.isRegistered -> {
                Log.i(TAG, "Device is registered — starting service and showing diagnostics")
                startForegroundService(Intent(this, EdgeAgentForegroundService::class.java))
                Intent(this, DiagnosticsActivity::class.java)
            }
            else -> {
                Log.i(TAG, "Device not registered — showing provisioning screen")
                Intent(this, ProvisioningActivity::class.java)
            }
        }

        target.flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        startActivity(target)
        finish()
    }
}
