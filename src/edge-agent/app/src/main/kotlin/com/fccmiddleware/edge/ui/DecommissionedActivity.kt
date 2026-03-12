package com.fccmiddleware.edge.ui

import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import androidx.activity.OnBackPressedCallback
import androidx.appcompat.app.AppCompatActivity

/**
 * DecommissionedActivity — shown when the cloud returns 403 DEVICE_DECOMMISSIONED.
 *
 * Per security spec:
 *   - All sync has been stopped permanently.
 *   - Refresh tokens are revoked server-side.
 *   - The device must be re-provisioned with a new bootstrap token from the portal.
 *
 * This screen is a dead end — the user must contact their supervisor to re-provision.
 */
class DecommissionedActivity : AppCompatActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        // Prevent navigating back — device is decommissioned
        // User must contact supervisor for re-provisioning
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() { /* no-op */ }
        })
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
                "All synchronization has been stopped.\n\n" +
                "Please contact your supervisor to re-provision this device with a new QR code."
            textSize = 16f
            gravity = Gravity.CENTER
            setPadding(0, 0, 0, padding)
        }
        layout.addView(message)

        val scrollView = ScrollView(this)
        scrollView.addView(layout)
        return scrollView
    }
}
