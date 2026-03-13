package com.fccmiddleware.edge.ui

import android.content.Intent
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import org.koin.android.ext.android.inject

/**
 * DecommissionedActivity — shown when the cloud returns 403 DEVICE_DECOMMISSIONED.
 *
 * Per security spec:
 *   - All sync has been stopped permanently.
 *   - Refresh tokens are revoked server-side.
 *   - The device must be re-provisioned with a new bootstrap token from the portal.
 *
 * Provides a "Re-Provision Device" button so the user can clear local state and
 * re-register without needing to manually clear app data or reinstall.
 */
class DecommissionedActivity : AppCompatActivity() {

    companion object {
        private const val TAG = "DecommissionedActivity"
    }

    private val encryptedPrefs: EncryptedPrefsManager by inject()
    private val keystoreManager: KeystoreManager by inject()
    private val bufferDatabase: BufferDatabase by inject()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        // Prevent navigating back — device is decommissioned
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() { /* no-op */ }
        })
    }

    /**
     * Clear all local credentials and navigate to the provisioning screen.
     * Requires a new bootstrap token from the portal to complete re-registration.
     *
     * AF-004: Explicitly stop EdgeAgentForegroundService before clearing credentials
     * to prevent START_STICKY from restarting the service with null/cleared credentials,
     * which would cause crashes or undefined behavior.
     */
    private fun startReProvisioning() {
        AppLogger.i(TAG, "User initiated re-provisioning from decommissioned state")

        // AF-004: Stop the foreground service BEFORE clearing credentials so a
        // START_STICKY restart cannot race with credential clearing.
        stopService(Intent(this, EdgeAgentForegroundService::class.java))

        // AF-013: Clear the Room database BEFORE clearing credentials to prevent
        // cross-site data contamination when re-provisioning for a different site.
        bufferDatabase.clearAllData()

        keystoreManager.clearAll()
        encryptedPrefs.clearAll()

        val intent = Intent(this, ProvisioningActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        }
        startActivity(intent)
        finish()
    }

    private fun buildLayout(): View {
        val padding = (24 * resources.displayMetrics.density).toInt()

        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(padding, padding, padding, padding)
        }

        val icon = TextView(this).apply {
            text = "X"
            textSize = 64f
            gravity = Gravity.CENTER
            setTextColor(0xFFCC0000.toInt())
            setPadding(0, 0, 0, padding)
        }
        layout.addView(icon)

        val title = TextView(this).apply {
            text = "Device Decommissioned"
            textSize = 24f
            gravity = Gravity.CENTER
            setTextColor(0xFFCC0000.toInt())
            setPadding(0, 0, 0, padding)
        }
        layout.addView(title)

        val message = TextView(this).apply {
            text = "This device has been decommissioned by the management portal.\n\n" +
                "All synchronization has been stopped and credentials have been revoked.\n\n" +
                "To re-activate this device:\n" +
                "1. Contact your site supervisor or IT administrator\n" +
                "2. Request a new provisioning QR code from the admin portal\n" +
                "3. Tap \"Re-Provision Device\" below and scan the new QR code"
            textSize = 16f
            gravity = Gravity.START
            setPadding(0, 0, 0, padding)
        }
        layout.addView(message)

        val reProvisionButton = Button(this).apply {
            text = "Re-Provision Device"
            textSize = 16f
            setPadding(padding, padding / 2, padding, padding / 2)
        }
        reProvisionButton.setOnClickListener {
            AlertDialog.Builder(this)
                .setTitle("Re-Provision Device?")
                .setMessage(
                    "This will clear all local data and credentials. " +
                    "You will need a new provisioning QR code from the admin portal to continue."
                )
                .setPositiveButton("Re-Provision") { _, _ -> startReProvisioning() }
                .setNegativeButton("Cancel", null)
                .show()
        }
        layout.addView(reProvisionButton)

        val scrollView = ScrollView(this)
        scrollView.addView(layout)
        return scrollView
    }
}
