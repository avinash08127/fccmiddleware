package com.fccmiddleware.edge.ui

import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.fccmiddleware.edge.R
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.config.LocalOverrideManager
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
    private val localOverrideManager: LocalOverrideManager by inject()

    // LR-001: Track active dialog to dismiss on destroy, consistent with SettingsActivity
    private var activeDialog: AlertDialog? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        // Prevent navigating back — device is decommissioned
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() { /* no-op */ }
        })
    }

    override fun onDestroy() {
        // LR-001: Dismiss any active dialog to prevent window leak
        activeDialog?.dismiss()
        activeDialog = null
        super.onDestroy()
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

        // AF-052: Clear local FCC overrides so the old site's host, port, and
        // credential settings do not leak to the new site after re-provisioning.
        localOverrideManager.clearAllOverrides()

        val intent = Intent(this, ProvisioningActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        }
        startActivity(intent)
        finish()
    }

    private fun buildLayout(): View {
        val dp = { value: Int -> (value * resources.displayMetrics.density).toInt() }
        val padding = dp(24)
        val halfPad = dp(12)
        val pumaRed = 0xFFE30613.toInt()
        val pumaGreen = 0xFF007A33.toInt()
        val darkText = 0xFF1A1A1A.toInt()
        val subtitleText = 0xFF4A4A4A.toInt()

        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(padding, padding, padding, padding)
            setBackgroundColor(Color.WHITE)
        }

        // Puma Energy logo
        layout.addView(ImageView(this).apply {
            setImageResource(R.drawable.puma_energy_logo)
            adjustViewBounds = true
            layoutParams = LinearLayout.LayoutParams(
                dp(160),
                LinearLayout.LayoutParams.WRAP_CONTENT,
            ).apply {
                gravity = Gravity.CENTER
                topMargin = dp(32)
                bottomMargin = halfPad
            }
        })

        // Warning icon
        val icon = TextView(this).apply {
            text = "X"
            textSize = 64f
            gravity = Gravity.CENTER
            setTypeface(typeface, Typeface.BOLD)
            setTextColor(pumaRed)
            setPadding(0, 0, 0, halfPad)
        }
        layout.addView(icon)

        val title = TextView(this).apply {
            text = "Device Decommissioned"
            textSize = 24f
            setTypeface(typeface, Typeface.BOLD)
            gravity = Gravity.CENTER
            setTextColor(pumaRed)
            setPadding(0, 0, 0, halfPad)
        }
        layout.addView(title)

        // Red accent divider
        layout.addView(View(this).apply {
            setBackgroundColor(pumaRed)
            layoutParams = LinearLayout.LayoutParams(
                dp(60),
                dp(3),
            ).apply {
                gravity = Gravity.CENTER
                bottomMargin = padding
            }
        })

        val message = TextView(this).apply {
            text = "This device has been decommissioned by the management portal.\n\n" +
                "All synchronization has been stopped and credentials have been revoked.\n\n" +
                "To re-activate this device:\n" +
                "1. Contact your site supervisor or IT administrator\n" +
                "2. Request a new provisioning QR code from the admin portal\n" +
                "3. Tap \"Re-Provision Device\" below and scan the new QR code"
            textSize = 16f
            setTextColor(subtitleText)
            gravity = Gravity.START
            setPadding(0, 0, 0, padding)
        }
        layout.addView(message)

        val reProvisionButton = Button(this).apply {
            text = "Re-Provision Device"
            textSize = 16f
            background = GradientDrawable().apply {
                setColor(pumaGreen)
                cornerRadius = 8 * resources.displayMetrics.density
            }
            setTextColor(Color.WHITE)
            isAllCaps = true
            setPadding(dp(32), halfPad, dp(32), halfPad)
        }
        reProvisionButton.setOnClickListener {
            // LR-001: Track dialog reference for lifecycle dismissal
            activeDialog?.dismiss()
            activeDialog = AlertDialog.Builder(this)
                .setTitle("Re-Provision Device?")
                .setMessage(
                    "This will clear all local data and credentials. " +
                    "You will need a new provisioning QR code from the admin portal to continue."
                )
                .setPositiveButton("Re-Provision") { _, _ ->
                    activeDialog = null
                    startReProvisioning()
                }
                .setNegativeButton("Cancel") { _, _ -> activeDialog = null }
                .setOnCancelListener { activeDialog = null }
                .show()
        }
        layout.addView(reProvisionButton)

        val scrollView = ScrollView(this).apply {
            setBackgroundColor(Color.WHITE)
        }
        scrollView.addView(layout)
        return scrollView
    }
}
