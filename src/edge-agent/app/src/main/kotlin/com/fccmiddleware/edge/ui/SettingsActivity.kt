package com.fccmiddleware.edge.ui

import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import com.fccmiddleware.edge.R
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import android.os.SystemClock
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.LocalOverrideManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.runtime.CadenceController
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.koin.android.ext.android.inject

/**
 * SettingsActivity — technician settings screen for FCC connection overrides.
 *
 * Accessible from DiagnosticsActivity. Allows viewing/editing FCC connection
 * parameters (IP, ports, access code) with local overrides that are applied
 * on top of cloud-delivered config.
 *
 * Editable fields: FCC IP, FCC Port, FCC JPL Port, FCC Access Code, WebSocket Port.
 * Read-only fields: Cloud Base URL, Environment, Device ID, Site Code.
 *
 * "Save & Reconnect" persists overrides and triggers FCC adapter reconnect.
 * "Reset to Cloud Defaults" clears all local overrides.
 */
class SettingsActivity : AppCompatActivity() {

    private val localOverrideManager: LocalOverrideManager by inject()
    private val configManager: ConfigManager by inject()
    private val encryptedPrefs: EncryptedPrefsManager by inject()
    private val cadenceController: CadenceController by inject()

    // Editable fields
    private lateinit var fccIpInput: EditText
    private lateinit var fccPortInput: EditText
    private lateinit var fccJplPortInput: EditText
    private lateinit var fccAccessCodeInput: EditText
    private lateinit var wsPortInput: EditText

    // Override indicators
    private lateinit var fccIpOverrideLabel: TextView
    private lateinit var fccPortOverrideLabel: TextView
    private lateinit var fccJplPortOverrideLabel: TextView
    private lateinit var fccAccessCodeOverrideLabel: TextView
    private lateinit var wsPortOverrideLabel: TextView

    // Read-only fields
    private lateinit var cloudBaseUrlValue: TextView
    private lateinit var environmentValue: TextView
    private lateinit var deviceIdValue: TextView
    private lateinit var siteCodeValue: TextView

    // Cloud API route display
    private val routeValueViews = mutableListOf<TextView>()

    // Status
    private lateinit var statusText: TextView

    // Debounce: track last save click time
    private var lastSaveClickTime = 0L

    // Track active dialog to dismiss on destroy
    private var activeDialog: AlertDialog? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.setFlags(
            WindowManager.LayoutParams.FLAG_SECURE,
            WindowManager.LayoutParams.FLAG_SECURE,
        )
        setContentView(buildLayout())

        // AT-008: Read EncryptedSharedPreferences off the main thread to prevent ANRs.
        // collectFieldData() reads from LocalOverrideManager and EncryptedPrefsManager
        // (both backed by EncryptedSharedPreferences which can block >100ms on slow storage).
        lifecycleScope.launch {
            val data = withContext(Dispatchers.IO) { collectFieldData() }
            applyFieldData(data)

            // Restore form state after rotation (overrides the async-populated values)
            savedInstanceState?.let { state ->
                fccIpInput.setText(state.getString(STATE_FCC_IP, fccIpInput.text.toString()))
                fccPortInput.setText(state.getString(STATE_FCC_PORT, fccPortInput.text.toString()))
                fccJplPortInput.setText(state.getString(STATE_FCC_JPL_PORT, fccJplPortInput.text.toString()))
                // LR-012: Access code is not restored from Bundle — re-entry required after
                // process death, consistent with AF-001 security posture.
                wsPortInput.setText(state.getString(STATE_WS_PORT, wsPortInput.text.toString()))
                val statusMsg = state.getString(STATE_STATUS_TEXT)
                if (!statusMsg.isNullOrEmpty()) {
                    statusText.text = statusMsg
                    statusText.setTextColor(state.getInt(STATE_STATUS_COLOR, COLOR_GREEN))
                    statusText.visibility = View.VISIBLE
                }
            }
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putString(STATE_FCC_IP, fccIpInput.text.toString())
        outState.putString(STATE_FCC_PORT, fccPortInput.text.toString())
        outState.putString(STATE_FCC_JPL_PORT, fccJplPortInput.text.toString())
        // LR-012: Do NOT persist FCC access code in the Bundle. Bundles are serialized
        // to the Binder transaction buffer and can survive process death in plaintext.
        // Consistent with AF-001 which avoids saving the provisioning token.
        outState.putString(STATE_WS_PORT, wsPortInput.text.toString())
        if (statusText.visibility == View.VISIBLE) {
            outState.putString(STATE_STATUS_TEXT, statusText.text.toString())
            outState.putInt(STATE_STATUS_COLOR, statusText.currentTextColor)
        }
    }

    override fun onDestroy() {
        activeDialog?.dismiss()
        activeDialog = null
        super.onDestroy()
    }

    // ── Field population (AT-008: split into IO-safe read + Main-thread apply) ─

    /**
     * AT-008: Holds all values read from EncryptedSharedPreferences and config.
     * [collectFieldData] populates this on Dispatchers.IO; [applyFieldData] applies
     * it to the UI on the Main thread.
     */
    private data class FieldData(
        val fccIp: String,
        val fccPort: String,
        val fccJplPort: String,
        val fccAccessCode: String,
        val fccAccessCodeHint: String,
        val wsPort: String,
        val fccIpOverridden: Boolean,
        val fccPortOverridden: Boolean,
        val fccJplPortOverridden: Boolean,
        val fccAccessCodeOverridden: Boolean,
        val wsPortOverridden: Boolean,
        val cloudBaseUrl: String,
        val environment: String,
        val deviceId: String,
        val siteCode: String,
        val routeBaseUrl: String,
    )

    /**
     * Read all display values from config, LocalOverrideManager, and EncryptedPrefsManager.
     * Safe to call on Dispatchers.IO — no UI access.
     */
    private fun collectFieldData(): FieldData {
        val config = configManager.config.value
        val cloudHost = config?.fcc?.hostAddress ?: encryptedPrefs.fccHost ?: ""
        val cloudPort = config?.fcc?.port
        val cloudJplPort: Int? = null // JPL port is override-only; no cloud field in FccDto
        val cloudCredential = config?.resolvedFccCredential() ?: ""
        val cloudWsPort: Int? = null // WS port is override-only

        return FieldData(
            fccIp = localOverrideManager.fccHost ?: cloudHost,
            fccPort = (localOverrideManager.fccPort ?: cloudPort)?.toString() ?: "",
            fccJplPort = (localOverrideManager.jplPort ?: cloudJplPort)?.toString() ?: "",
            fccAccessCode = localOverrideManager.fccCredential ?: "",
            fccAccessCodeHint = if (cloudCredential.isNotEmpty()) "Cloud credential set (hidden)" else "Access code",
            wsPort = (localOverrideManager.wsPort ?: cloudWsPort)?.toString() ?: "",
            fccIpOverridden = localOverrideManager.fccHost != null,
            fccPortOverridden = localOverrideManager.fccPort != null,
            fccJplPortOverridden = localOverrideManager.jplPort != null,
            fccAccessCodeOverridden = localOverrideManager.fccCredential != null,
            wsPortOverridden = localOverrideManager.wsPort != null,
            cloudBaseUrl = config?.sync?.cloudBaseUrl ?: encryptedPrefs.cloudBaseUrl ?: "Not configured",
            environment = deriveEnvironment(config?.sync?.cloudBaseUrl ?: encryptedPrefs.cloudBaseUrl),
            deviceId = encryptedPrefs.deviceId ?: "Not provisioned",
            siteCode = config?.identity?.siteCode ?: encryptedPrefs.siteCode ?: "Not provisioned",
            routeBaseUrl = (config?.sync?.cloudBaseUrl ?: encryptedPrefs.cloudBaseUrl ?: "").trimEnd('/'),
        )
    }

    /**
     * Apply pre-read field data to the UI. Must be called on the Main thread.
     */
    private fun applyFieldData(data: FieldData) {
        fccIpInput.setText(data.fccIp)
        fccPortInput.setText(data.fccPort)
        fccJplPortInput.setText(data.fccJplPort)
        // Only populate credential field if there's a local override;
        // never pre-fill the cloud credential to avoid plaintext exposure.
        fccAccessCodeInput.setText(data.fccAccessCode)
        fccAccessCodeInput.hint = data.fccAccessCodeHint
        wsPortInput.setText(data.wsPort)

        // Override indicators
        fccIpOverrideLabel.visibility = if (data.fccIpOverridden) View.VISIBLE else View.GONE
        fccPortOverrideLabel.visibility = if (data.fccPortOverridden) View.VISIBLE else View.GONE
        fccJplPortOverrideLabel.visibility = if (data.fccJplPortOverridden) View.VISIBLE else View.GONE
        fccAccessCodeOverrideLabel.visibility = if (data.fccAccessCodeOverridden) View.VISIBLE else View.GONE
        wsPortOverrideLabel.visibility = if (data.wsPortOverridden) View.VISIBLE else View.GONE

        // Read-only fields
        cloudBaseUrlValue.text = data.cloudBaseUrl
        environmentValue.text = data.environment
        deviceIdValue.text = data.deviceId
        siteCodeValue.text = data.siteCode

        // Cloud API Routes
        CLOUD_API_ROUTES.forEachIndexed { index, (_, path) ->
            routeValueViews[index].text = if (data.routeBaseUrl.isNotEmpty()) "${data.routeBaseUrl}$path" else "Not configured"
        }
    }

    private fun deriveEnvironment(cloudUrl: String?): String {
        if (cloudUrl == null) return "Unknown"
        return when {
            cloudUrl.contains("staging", ignoreCase = true) -> "Staging"
            cloudUrl.contains("dev", ignoreCase = true) -> "Development"
            cloudUrl.contains("uat", ignoreCase = true) -> "UAT"
            else -> "Production"
        }
    }

    private fun com.fccmiddleware.edge.config.EdgeAgentConfigDto.resolvedFccCredential(): String =
        when {
            !fcc.secretEnvelope.payload.isNullOrBlank() -> fcc.secretEnvelope.payload
            !fcc.credentialRef.isNullOrBlank() -> fcc.credentialRef
            else -> ""
        }

    // ── Save & Reconnect ─────────────────────────────────────────────────────

    private fun saveAndReconnect() {
        // Debounce: ignore rapid double-taps
        val now = SystemClock.elapsedRealtime()
        if (now - lastSaveClickTime < DEBOUNCE_MS) return
        lastSaveClickTime = now

        val errors = mutableListOf<String>()

        val fccIp = fccIpInput.text.toString().trim()
        val fccPort = fccPortInput.text.toString().trim()
        val fccJplPort = fccJplPortInput.text.toString().trim()
        val fccAccessCode = fccAccessCodeInput.text.toString().trim()
        val wsPort = wsPortInput.text.toString().trim()

        // Validate FCC IP
        if (fccIp.isNotEmpty() && !LocalOverrideManager.isValidHostOrIp(fccIp)) {
            errors += "FCC IP: must be a valid IPv4 address or hostname"
        }

        // Validate ports
        if (fccPort.isNotEmpty()) {
            val port = fccPort.toIntOrNull()
            if (port == null || !LocalOverrideManager.isValidPort(port)) {
                errors += "FCC Port: must be a number between 1 and 65535"
            }
        }
        if (fccJplPort.isNotEmpty()) {
            val port = fccJplPort.toIntOrNull()
            if (port == null || !LocalOverrideManager.isValidPort(port)) {
                errors += "FCC JPL Port: must be a number between 1 and 65535"
            }
        }
        if (wsPort.isNotEmpty()) {
            val port = wsPort.toIntOrNull()
            if (port == null || !LocalOverrideManager.isValidPort(port)) {
                errors += "WebSocket Port: must be a number between 1 and 65535"
            }
        }

        // AF-007: Cross-field validation — reject duplicate port assignments
        val portEntries = mutableListOf<Pair<String, Int>>()
        fccPort.toIntOrNull()?.let { portEntries += "FCC Port" to it }
        fccJplPort.toIntOrNull()?.let { portEntries += "FCC JPL Port" to it }
        wsPort.toIntOrNull()?.let { portEntries += "WebSocket Port" to it }
        val seen = mutableMapOf<Int, String>()
        for ((name, port) in portEntries) {
            val existing = seen[port]
            if (existing != null) {
                errors += "$name conflicts with $existing (both use port $port)"
            } else {
                seen[port] = name
            }
        }

        if (errors.isNotEmpty()) {
            statusText.text = errors.joinToString("\n")
            statusText.setTextColor(COLOR_RED)
            statusText.visibility = View.VISIBLE
            return
        }

        // Save overrides off the main thread (EncryptedSharedPreferences writes can
        // block 10-50 ms due to encryption — P-005)
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                if (fccIp.isNotEmpty()) {
                    localOverrideManager.saveOverride(LocalOverrideManager.KEY_FCC_HOST, fccIp)
                } else {
                    localOverrideManager.clearOverride(LocalOverrideManager.KEY_FCC_HOST)
                }

                if (fccPort.isNotEmpty()) {
                    localOverrideManager.saveOverride(LocalOverrideManager.KEY_FCC_PORT, fccPort)
                } else {
                    localOverrideManager.clearOverride(LocalOverrideManager.KEY_FCC_PORT)
                }

                if (fccJplPort.isNotEmpty()) {
                    localOverrideManager.saveOverride(LocalOverrideManager.KEY_FCC_JPL_PORT, fccJplPort)
                } else {
                    localOverrideManager.clearOverride(LocalOverrideManager.KEY_FCC_JPL_PORT)
                }

                if (fccAccessCode.isNotEmpty()) {
                    localOverrideManager.saveOverride(LocalOverrideManager.KEY_FCC_CREDENTIAL, fccAccessCode)
                } else {
                    localOverrideManager.clearOverride(LocalOverrideManager.KEY_FCC_CREDENTIAL)
                }

                if (wsPort.isNotEmpty()) {
                    localOverrideManager.saveOverride(LocalOverrideManager.KEY_WS_PORT, wsPort)
                } else {
                    localOverrideManager.clearOverride(LocalOverrideManager.KEY_WS_PORT)
                }

                AppLogger.i(TAG, "Overrides saved, requesting FCC reconnect")
                cadenceController.requestFccReconnect()

                // AT-008: Read field data while still on IO dispatcher
                val fieldData = collectFieldData()
                withContext(Dispatchers.Main) {
                    // LR-003: Guard against UI updates on a finishing/destroyed Activity
                    if (isFinishing || isDestroyed) return@withContext
                    statusText.text = "Settings saved. FCC reconnecting..."
                    statusText.setTextColor(COLOR_GREEN)
                    statusText.visibility = View.VISIBLE
                    applyFieldData(fieldData)
                    Toast.makeText(this@SettingsActivity, "Settings saved & reconnecting", Toast.LENGTH_SHORT).show()
                }
            } catch (e: Exception) {
                AppLogger.e(TAG, "Failed to save overrides", e)
                withContext(Dispatchers.Main) {
                    if (isFinishing || isDestroyed) return@withContext
                    statusText.text = "Error: ${e.message}"
                    statusText.setTextColor(COLOR_RED)
                    statusText.visibility = View.VISIBLE
                }
            }
        }
    }

    // ── Reset to Cloud Defaults ──────────────────────────────────────────────

    private fun resetToCloudDefaults() {
        activeDialog?.dismiss()
        activeDialog = AlertDialog.Builder(this)
            .setTitle("Reset to Cloud Defaults")
            .setMessage("Clear all local overrides and revert to cloud-delivered configuration?")
            .setPositiveButton("Reset") { _, _ ->
                lifecycleScope.launch(Dispatchers.IO) {
                    localOverrideManager.clearAllOverrides()
                    AppLogger.i(TAG, "All overrides cleared, requesting FCC reconnect")
                    cadenceController.requestFccReconnect()
                    // AT-008: Read field data while still on IO dispatcher
                    val fieldData = collectFieldData()
                    withContext(Dispatchers.Main) {
                        // LR-002: Guard against UI updates on a finishing/destroyed Activity
                        if (isFinishing || isDestroyed) return@withContext
                        applyFieldData(fieldData)
                        statusText.text = "Overrides cleared. Using cloud defaults."
                        statusText.setTextColor(COLOR_GREEN)
                        statusText.visibility = View.VISIBLE
                        Toast.makeText(this@SettingsActivity, "Reset to cloud defaults", Toast.LENGTH_SHORT).show()
                        activeDialog = null
                    }
                }
            }
            .setNegativeButton("Cancel") { _, _ -> activeDialog = null }
            .setOnCancelListener { activeDialog = null }
            .show()
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private fun buildLayout(): View {
        val padding = dp(16)
        val halfPad = dp(8)

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(padding, padding, padding, padding)
            setBackgroundColor(Color.WHITE)
        }

        // Puma Energy logo
        root.addView(ImageView(this).apply {
            setImageResource(R.drawable.puma_energy_logo)
            adjustViewBounds = true
            layoutParams = LinearLayout.LayoutParams(
                dp(140),
                LinearLayout.LayoutParams.WRAP_CONTENT,
            ).apply {
                gravity = Gravity.CENTER
                topMargin = dp(32)
                bottomMargin = halfPad
            }
        })

        // Title
        root.addView(TextView(this).apply {
            text = "FCC Connection Settings"
            textSize = 20f
            setTypeface(null, Typeface.BOLD)
            setTextColor(COLOR_TEXT)
            gravity = Gravity.CENTER
            setPadding(0, 0, 0, halfPad)
        })

        // Red accent divider
        root.addView(View(this).apply {
            setBackgroundColor(PUMA_RED)
            layoutParams = LinearLayout.LayoutParams(
                dp(60),
                dp(3),
            ).apply {
                gravity = Gravity.CENTER
                bottomMargin = padding
            }
        })

        // ── Editable section ────────────────────────────────────────────
        root.addView(makeSectionHeader("FCC Connection Overrides"))

        fccIpOverrideLabel = makeOverrideIndicator()
        fccIpInput = EditText(this).apply {
            hint = "e.g., 192.168.1.100"
            inputType = InputType.TYPE_CLASS_TEXT
            setSingleLine()
            setPadding(halfPad, halfPad, halfPad, halfPad)
        }
        root.addView(makeFieldGroup("FCC IP / Hostname", fccIpInput, fccIpOverrideLabel))

        fccPortOverrideLabel = makeOverrideIndicator()
        fccPortInput = EditText(this).apply {
            hint = "e.g., 10001"
            inputType = InputType.TYPE_CLASS_NUMBER
            setSingleLine()
            setPadding(halfPad, halfPad, halfPad, halfPad)
        }
        root.addView(makeFieldGroup("FCC Port", fccPortInput, fccPortOverrideLabel))

        fccJplPortOverrideLabel = makeOverrideIndicator()
        fccJplPortInput = EditText(this).apply {
            hint = "e.g., 10002"
            inputType = InputType.TYPE_CLASS_NUMBER
            setSingleLine()
            setPadding(halfPad, halfPad, halfPad, halfPad)
        }
        root.addView(makeFieldGroup("FCC JPL Port", fccJplPortInput, fccJplPortOverrideLabel))

        fccAccessCodeOverrideLabel = makeOverrideIndicator()
        fccAccessCodeInput = EditText(this).apply {
            hint = "Access code" // updated dynamically in applyFieldData()
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            setSingleLine()
            setPadding(halfPad, halfPad, halfPad, halfPad)
        }
        root.addView(makeFieldGroup("FCC Access Code", fccAccessCodeInput, fccAccessCodeOverrideLabel))

        wsPortOverrideLabel = makeOverrideIndicator()
        wsPortInput = EditText(this).apply {
            hint = "e.g., 8080"
            inputType = InputType.TYPE_CLASS_NUMBER
            setSingleLine()
            setPadding(halfPad, halfPad, halfPad, halfPad)
        }
        root.addView(makeFieldGroup("WebSocket Port", wsPortInput, wsPortOverrideLabel))

        // ── Read-only section ───────────────────────────────────────────
        root.addView(makeSectionHeader("Device Information (Read-Only)"))

        cloudBaseUrlValue = makeReadOnlyValue()
        root.addView(makeReadOnlyRow("Cloud Base URL:", cloudBaseUrlValue))

        environmentValue = makeReadOnlyValue()
        root.addView(makeReadOnlyRow("Environment:", environmentValue))

        deviceIdValue = makeReadOnlyValue()
        root.addView(makeReadOnlyRow("Device ID:", deviceIdValue))

        siteCodeValue = makeReadOnlyValue()
        root.addView(makeReadOnlyRow("Site Code:", siteCodeValue))

        // ── Cloud API Routes section ────────────────────────────────────
        root.addView(makeSectionHeader("Cloud API Routes (Read-Only)"))

        routeValueViews.clear()
        for ((name, _) in CLOUD_API_ROUTES) {
            val valueView = TextView(this).apply {
                textSize = 12f
                setTypeface(Typeface.MONOSPACE, Typeface.NORMAL)
                setTextColor(COLOR_GRAY)
            }
            routeValueViews.add(valueView)
            root.addView(makeRouteRow(name, valueView))
        }

        // ── Status text ─────────────────────────────────────────────────
        statusText = TextView(this).apply {
            visibility = View.GONE
            textSize = 13f
            setPadding(0, padding, 0, 0)
        }
        root.addView(statusText)

        // ── Buttons ─────────────────────────────────────────────────────
        root.addView(View(this).apply { minimumHeight = dp(16) })

        root.addView(Button(this).apply {
            text = "Save & Reconnect"
            textSize = 16f
            setOnClickListener { saveAndReconnect() }
            background = GradientDrawable().apply {
                setColor(PUMA_GREEN)
                cornerRadius = 8 * resources.displayMetrics.density
            }
            setTextColor(Color.WHITE)
            isAllCaps = true
            setPadding(dp(32), halfPad, dp(32), halfPad)
        })

        root.addView(View(this).apply { minimumHeight = dp(8) })

        root.addView(Button(this).apply {
            text = "Reset to Cloud Defaults"
            textSize = 16f
            setOnClickListener { resetToCloudDefaults() }
            background = GradientDrawable().apply {
                setColor(Color.WHITE)
                setStroke(dp(2), PUMA_RED)
                cornerRadius = 8 * resources.displayMetrics.density
            }
            setTextColor(PUMA_RED)
            isAllCaps = true
            setPadding(dp(32), halfPad, dp(32), halfPad)
        })

        root.addView(View(this).apply { minimumHeight = dp(8) })

        root.addView(Button(this).apply {
            text = "Back"
            textSize = 16f
            setOnClickListener { finish() }
            background = GradientDrawable().apply {
                setColor(Color.WHITE)
                setStroke(dp(2), PUMA_GREEN)
                cornerRadius = 8 * resources.displayMetrics.density
            }
            setTextColor(PUMA_GREEN)
            isAllCaps = true
            setPadding(dp(32), halfPad, dp(32), halfPad)
        })

        val scrollView = ScrollView(this).apply {
            setBackgroundColor(Color.WHITE)
        }
        scrollView.addView(root)
        return scrollView
    }

    // ── Layout helpers ───────────────────────────────────────────────────────

    private fun makeSectionHeader(title: String): TextView {
        return TextView(this).apply {
            text = title
            textSize = 15f
            setTypeface(null, Typeface.BOLD)
            setPadding(0, dp(12), 0, dp(4))
            setTextColor(PUMA_GREEN)
        }
    }

    private fun makeFieldGroup(label: String, input: EditText, overrideIndicator: TextView): LinearLayout {
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(4), 0, dp(4))

            val labelRow = LinearLayout(this@SettingsActivity).apply {
                orientation = LinearLayout.HORIZONTAL
                addView(TextView(this@SettingsActivity).apply {
                    text = label
                    textSize = 14f
                    setTextColor(COLOR_LABEL)
                })
                addView(overrideIndicator)
            }
            addView(labelRow)
            addView(input)
        }
    }

    private fun makeOverrideIndicator(): TextView {
        return TextView(this).apply {
            text = "  (overridden)"
            textSize = 12f
            setTextColor(COLOR_OVERRIDE)
            visibility = View.GONE
        }
    }

    private fun makeReadOnlyRow(label: String, valueView: TextView): LinearLayout {
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, dp(4), 0, dp(4))
            addView(TextView(this@SettingsActivity).apply {
                text = label
                textSize = 14f
                setTextColor(COLOR_LABEL)
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            })
            addView(valueView.apply {
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1.5f)
                gravity = Gravity.END
            })
        }
    }

    private fun makeReadOnlyValue(): TextView {
        return TextView(this).apply {
            textSize = 13f
            setTypeface(Typeface.MONOSPACE, Typeface.NORMAL)
            setTextColor(COLOR_GRAY)
        }
    }

    private fun makeRouteRow(label: String, valueView: TextView): LinearLayout {
        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(2), 0, dp(2))
            addView(TextView(this@SettingsActivity).apply {
                text = label
                textSize = 12f
                setTextColor(COLOR_LABEL)
            })
            addView(valueView)
        }
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    companion object {
        private const val TAG = "SettingsActivity"
        private const val DEBOUNCE_MS = 1000L

        // Instance state keys
        private const val STATE_FCC_IP = "state_fcc_ip"
        private const val STATE_FCC_PORT = "state_fcc_port"
        private const val STATE_FCC_JPL_PORT = "state_fcc_jpl_port"
        private const val STATE_FCC_ACCESS_CODE = "state_fcc_access_code"
        private const val STATE_WS_PORT = "state_ws_port"
        private const val STATE_STATUS_TEXT = "state_status_text"
        private const val STATE_STATUS_COLOR = "state_status_color"

        private const val PUMA_GREEN = 0xFF007A33.toInt()
        private const val PUMA_RED = 0xFFE30613.toInt()
        private const val COLOR_GREEN = 0xFF2E7D32.toInt()
        private const val COLOR_RED = 0xFFC62828.toInt()
        private const val COLOR_GRAY = 0xFF9E9E9E.toInt()
        private const val COLOR_TEXT = 0xFF1A1A1A.toInt()
        private const val COLOR_LABEL = 0xFF4A4A4A.toInt()
        private const val COLOR_OVERRIDE = 0xFFFF6F00.toInt()

        private val CLOUD_API_ROUTES = listOf(
            "Registration" to "/api/v1/agent/register",
            "Config Poll" to "/api/v1/agent/config",
            "Token Refresh" to "/api/v1/agent/token/refresh",
            "Transaction Upload" to "/api/v1/transactions/upload",
            "Synced Status" to "/api/v1/transactions/synced-status",
            "Pre-Auth Forward" to "/api/v1/preauth",
            "Telemetry" to "/api/v1/agent/telemetry",
            "Diagnostic Logs" to "/api/v1/agent/diagnostic-logs",
            "Version Check" to "/api/v1/agent/version-check",
        )
    }
}
