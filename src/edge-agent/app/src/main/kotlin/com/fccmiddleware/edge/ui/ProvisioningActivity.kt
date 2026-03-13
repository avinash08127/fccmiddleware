package com.fccmiddleware.edge.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.text.InputType
import com.fccmiddleware.edge.logging.AppLogger
import android.view.Gravity
import android.view.View
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.Spinner
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.fccmiddleware.edge.config.CloudEnvironments
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import com.fccmiddleware.edge.security.Sensitive
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanIntentResult
import com.journeyapps.barcodescanner.ScanOptions
import androidx.activity.OnBackPressedCallback
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonPrimitive
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import org.koin.android.ext.android.inject
import org.koin.androidx.viewmodel.ext.android.viewModel as koinViewModel

/**
 * ProvisioningActivity — QR code scanner and manual entry device registration flow.
 *
 * Shown on first launch (or after factory reset) when no registration exists.
 * Two provisioning paths:
 *   A. QR code scan (preferred): camera scans QR containing { v, sc, cu, pt } (v1)
 *      or { v, sc, cu, pt, env } (v2 — resolves URL from CloudEnvironments)
 *   B. Manual entry (fallback): user picks environment from dropdown, enters Site Code and Token
 * Both paths then:
 *   1. Collect device fingerprint (serial, model, OS, app version)
 *   2. Call POST /api/v1/agent/register
 *   3. On success: store tokens in Keystore, identity in EncryptedPrefs, config in Room
 *   4. Start EdgeAgentForegroundService
 *   5. Navigate to DiagnosticsActivity
 */
class ProvisioningActivity : AppCompatActivity() {

    private val provisioningViewModel: ProvisioningViewModel by koinViewModel()
    private val encryptedPrefs: EncryptedPrefsManager by inject()

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
    }

    // UI elements — method selection screen
    private lateinit var methodPanel: LinearLayout
    private lateinit var scanButton: Button
    private lateinit var manualEntryButton: Button

    // UI elements — manual entry screen
    private lateinit var manualPanel: LinearLayout
    private lateinit var environmentSpinner: Spinner
    private lateinit var cloudUrlInput: EditText
    private lateinit var siteCodeInput: EditText
    private lateinit var tokenInput: EditText
    private lateinit var manualRegisterButton: Button
    private lateinit var manualBackButton: Button

    // UI elements — shared status area
    private lateinit var statusText: TextView
    private lateinit var errorText: TextView
    private lateinit var progressBar: ProgressBar

    // QR scanner launcher
    private val scanLauncher = registerForActivityResult(ScanContract()) { result ->
        onScanResult(result)
    }

    companion object {
        private const val TAG = "ProvisioningActivity"
        private const val CAMERA_PERMISSION_REQUEST = 100
        private const val STATE_CLOUD_URL = "state_cloud_url"
        private const val STATE_SITE_CODE = "state_site_code"
        private const val STATE_ENV_INDEX = "state_env_index"
        private const val STATE_MANUAL_VISIBLE = "state_manual_visible"

        /** AF-014: Intent extra key indicating why re-provisioning was triggered. */
        const val EXTRA_REASON = "extra_reprovisioning_reason"
        const val REASON_TOKEN_EXPIRED = "token_expired"
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // AF-011: If the device is already registered (e.g., activity recreated after a
        // config change during the Success → finish() window), skip straight to diagnostics.
        if (encryptedPrefs.isRegistered) {
            AppLogger.i(TAG, "Device already registered — redirecting to DiagnosticsActivity")
            try {
                val serviceIntent = Intent(this, EdgeAgentForegroundService::class.java)
                startForegroundService(serviceIntent)
            } catch (e: Exception) {
                AppLogger.e(TAG, "Failed to start foreground service on redirect", e)
            }
            val intent = Intent(this, DiagnosticsActivity::class.java)
            intent.flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
            startActivity(intent)
            finish()
            return
        }

        setContentView(buildLayout())

        // AF-014: Show contextual banner when re-provisioning is triggered by token expiry
        if (intent?.getStringExtra(EXTRA_REASON) == REASON_TOKEN_EXPIRED) {
            showError("Your device's authentication has expired. Please scan a new provisioning QR code from the admin portal.")
        }

        scanButton.setOnClickListener { startQrScan() }
        manualEntryButton.setOnClickListener { showManualEntryScreen() }
        manualBackButton.setOnClickListener { showMethodSelectionScreen() }
        manualRegisterButton.setOnClickListener { submitManualEntry() }

        // Restore form state after rotation/process death
        savedInstanceState?.let { state ->
            if (state.getBoolean(STATE_MANUAL_VISIBLE, false)) {
                showManualEntryScreen()
                environmentSpinner.setSelection(state.getInt(STATE_ENV_INDEX, 0))
                cloudUrlInput.setText(state.getString(STATE_CLOUD_URL, ""))
                siteCodeInput.setText(state.getString(STATE_SITE_CODE, ""))
                // AF-001: Token is intentionally NOT restored — user must re-enter after process death.
            }
        }

        // Handle back press: if manual panel is showing, go back to method selection
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                val registering = provisioningViewModel.registrationState.value is ProvisioningViewModel.RegistrationState.InProgress
                if (manualPanel.visibility == View.VISIBLE && !registering) {
                    showMethodSelectionScreen()
                } else if (!registering) {
                    isEnabled = false
                    onBackPressedDispatcher.onBackPressed()
                }
                // If registering, ignore back press to avoid interrupting the flow
            }
        })

        // Observe ViewModel state — must come last so that savedInstanceState panel/field
        // restoration runs first, then the current registration state is applied on top.
        lifecycleScope.launch {
            provisioningViewModel.registrationState.collect { state ->
                when (state) {
                    is ProvisioningViewModel.RegistrationState.Idle -> Unit
                    is ProvisioningViewModel.RegistrationState.InProgress -> {
                        showProgress(state.message)
                        scanButton.isEnabled = false
                        manualRegisterButton.isEnabled = false
                        manualBackButton.isEnabled = false
                    }
                    is ProvisioningViewModel.RegistrationState.Error -> {
                        showError(state.message)
                        scanButton.isEnabled = true
                        manualRegisterButton.isEnabled = true
                        manualBackButton.isEnabled = true
                    }
                    is ProvisioningViewModel.RegistrationState.Success -> {
                        showProgress("Starting Edge Agent service...")
                        try {
                            val serviceIntent = Intent(
                                this@ProvisioningActivity,
                                EdgeAgentForegroundService::class.java,
                            )
                            startForegroundService(serviceIntent)
                        } catch (e: Exception) {
                            AppLogger.e(TAG, "Failed to start foreground service — will retry from DiagnosticsActivity", e)
                        }
                        val intent = Intent(this@ProvisioningActivity, DiagnosticsActivity::class.java)
                        intent.flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
                        startActivity(intent)
                        finish()
                        // AF-011: Reset state AFTER finish() so a config-change recreation
                        // still sees Success (and the isRegistered guard above handles it).
                        provisioningViewModel.onNavigationComplete()
                    }
                }
            }
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putBoolean(STATE_MANUAL_VISIBLE, manualPanel.visibility == View.VISIBLE)
        outState.putInt(STATE_ENV_INDEX, environmentSpinner.selectedItemPosition)
        outState.putString(STATE_CLOUD_URL, cloudUrlInput.text.toString())
        outState.putString(STATE_SITE_CODE, siteCodeInput.text.toString())
        // AF-001: Do NOT persist the provisioning token in the Bundle. Bundles are
        // serialized to the Binder transaction buffer and can survive process death
        // in plaintext. The user must re-enter the token after process death.
    }

    // ── Screen navigation ────────────────────────────────────────────────────

    private fun showMethodSelectionScreen() {
        methodPanel.visibility = View.VISIBLE
        manualPanel.visibility = View.GONE
        errorText.visibility = View.GONE
        statusText.visibility = View.GONE
        progressBar.visibility = View.GONE
    }

    private fun showManualEntryScreen() {
        methodPanel.visibility = View.GONE
        manualPanel.visibility = View.VISIBLE
        errorText.visibility = View.GONE
        statusText.visibility = View.GONE
        progressBar.visibility = View.GONE
        manualRegisterButton.isEnabled = true
        manualBackButton.isEnabled = true
    }

    // ── QR code scan path ────────────────────────────────────────────────────

    private fun startQrScan() {
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
            != PackageManager.PERMISSION_GRANTED
        ) {
            ActivityCompat.requestPermissions(
                this,
                arrayOf(Manifest.permission.CAMERA),
                CAMERA_PERMISSION_REQUEST,
            )
            return
        }
        launchScanner()
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == CAMERA_PERMISSION_REQUEST &&
            grantResults.isNotEmpty() &&
            grantResults[0] == PackageManager.PERMISSION_GRANTED
        ) {
            launchScanner()
        } else {
            showError("Camera permission is required to scan provisioning QR codes.\nYou can use manual entry instead.")
        }
    }

    private fun launchScanner() {
        val options = ScanOptions().apply {
            setDesiredBarcodeFormats(ScanOptions.QR_CODE)
            setPrompt("Scan the provisioning QR code")
            setBeepEnabled(false)
            setOrientationLocked(true)
        }
        scanLauncher.launch(options)
    }

    private fun onScanResult(result: ScanIntentResult) {
        val contents = result.contents
        if (contents == null) {
            showError("QR scan cancelled. You can try again or use manual entry.")
            return
        }

        val qrData = parseQrPayload(contents)
        if (qrData == null) {
            showError("Invalid QR code format. Expected provisioning QR with v, sc, cu, pt fields.")
            return
        }

        provisioningViewModel.register(qrData)
    }

    // ── Manual entry path ────────────────────────────────────────────────────

    private fun submitManualEntry() {
        val selectedEnvIndex = environmentSpinner.selectedItemPosition
        val envKey = CloudEnvironments.keys[selectedEnvIndex]
        val resolvedUrl = CloudEnvironments.resolve(envKey)
        // Use the resolved environment URL; fall back to the text field for LOCAL or custom URLs
        val cloudUrl = resolvedUrl ?: cloudUrlInput.text.toString().trim()

        val siteCode = siteCodeInput.text.toString().trim()
        val token = tokenInput.text.toString().trim()

        if (cloudUrl.isBlank()) {
            showError("Please enter the Cloud URL.")
            return
        }
        if (!cloudUrl.startsWith("https://")) {
            showError("Cloud URL must start with https://")
            return
        }
        if (siteCode.isBlank()) {
            showError("Please enter the Site Code.")
            return
        }
        if (token.isBlank()) {
            showError("Please enter the Provisioning Token.")
            return
        }

        AppLogger.i(TAG, "Manual entry submitted, env=$envKey, starting registration")
        val bootstrapData = QrBootstrapData(
            siteCode = siteCode,
            cloudBaseUrl = cloudUrl.trimEnd('/'),
            provisioningToken = token,
            environment = envKey,
        )
        provisioningViewModel.register(bootstrapData)
    }

    // ── QR payload parsing ───────────────────────────────────────────────────

    /**
     * Parse the QR code JSON payload.
     * v1 format: { "v": 1, "sc": "SITE-CODE", "cu": "https://...", "pt": "token" }
     * v2 format: { "v": 2, "sc": "SITE-CODE", "cu": "https://...", "pt": "token", "env": "STAGING" }
     *
     * v2 with env: resolve URL from CloudEnvironments, fall back to cu if env is unknown.
     * v2 without env (or v1): use cu directly (backward compatible).
     */
    internal fun parseQrPayload(rawJson: String): QrBootstrapData? {
        return try {
            val obj = json.decodeFromString<JsonObject>(rawJson)
            val version = obj["v"]?.jsonPrimitive?.int
            val siteCode = obj["sc"]?.jsonPrimitive?.content
            val cloudUrl = obj["cu"]?.jsonPrimitive?.content
            val token = obj["pt"]?.jsonPrimitive?.content
            val env = obj["env"]?.jsonPrimitive?.contentOrNull

            if (version == null || version !in 1..2) {
                AppLogger.w(TAG, "Unsupported QR version: $version")
                return null
            }
            if (siteCode.isNullOrBlank() || token.isNullOrBlank()) {
                AppLogger.w(TAG, "Missing required QR fields")
                return null
            }

            // Resolve the effective cloud URL
            val resolvedUrl = if (version == 2 && !env.isNullOrBlank()) {
                // v2 with env field: try built-in map first, fall back to explicit cu
                CloudEnvironments.resolve(env) ?: run {
                    AppLogger.w(TAG, "Unknown env '$env', falling back to cu field")
                    cloudUrl
                }
            } else {
                // v1, or v2 without env: use cu directly
                cloudUrl
            }

            if (resolvedUrl.isNullOrBlank()) {
                AppLogger.w(TAG, "No cloud URL available (no env match and no cu field)")
                return null
            }

            if (!resolvedUrl.startsWith("https://")) {
                AppLogger.w(TAG, "Cloud URL must use HTTPS — rejecting insecure QR code")
                return null
            }

            QrBootstrapData(
                siteCode = siteCode,
                cloudBaseUrl = resolvedUrl.trimEnd('/'),
                provisioningToken = token,
                environment = env?.uppercase(),
            )
        } catch (e: Exception) {
            AppLogger.w(TAG, "Failed to parse QR payload")
            null
        }
    }

    private fun showProgress(message: String) {
        statusText.text = message
        statusText.visibility = View.VISIBLE
        progressBar.visibility = View.VISIBLE
        errorText.visibility = View.GONE
    }

    private fun showError(message: String) {
        errorText.text = message
        errorText.visibility = View.VISIBLE
        progressBar.visibility = View.GONE
        statusText.visibility = View.GONE
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private fun buildLayout(): View {
        val padding = (16 * resources.displayMetrics.density).toInt()
        val halfPad = padding / 2

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(padding, padding, padding, padding)
        }

        // ── Title (always visible)
        val title = TextView(this).apply {
            text = "Puma Energy FCC Agent"
            textSize = 22f
            gravity = Gravity.CENTER
            setPadding(0, 0, 0, padding)
        }
        root.addView(title)

        // ── Method selection panel ──────────────────────────────────
        methodPanel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
        }

        val description = TextView(this).apply {
            text = "Choose how to register this device with the cloud."
            textSize = 16f
            gravity = Gravity.CENTER
            setPadding(0, 0, 0, padding * 2)
        }
        methodPanel.addView(description)

        scanButton = Button(this).apply {
            text = "Scan QR Code"
            textSize = 18f
        }
        methodPanel.addView(scanButton)

        val orLabel = TextView(this).apply {
            text = "— or —"
            textSize = 14f
            gravity = Gravity.CENTER
            setPadding(0, halfPad, 0, halfPad)
            setTextColor(0xFF888888.toInt())
        }
        methodPanel.addView(orLabel)

        manualEntryButton = Button(this).apply {
            text = "Enter Manually"
            textSize = 16f
        }
        methodPanel.addView(manualEntryButton)

        root.addView(methodPanel)

        // ── Manual entry panel (initially hidden) ───────────────────
        manualPanel = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            visibility = View.GONE
        }

        val manualTitle = TextView(this).apply {
            text = "Manual Provisioning"
            textSize = 18f
            gravity = Gravity.CENTER
            setPadding(0, 0, 0, halfPad)
        }
        manualPanel.addView(manualTitle)

        val manualDesc = TextView(this).apply {
            text = "Enter the provisioning details from the admin portal."
            textSize = 14f
            gravity = Gravity.CENTER
            setTextColor(0xFF666666.toInt())
            setPadding(0, 0, 0, padding)
        }
        manualPanel.addView(manualDesc)

        val envLabel = TextView(this).apply {
            text = "Environment"
            textSize = 14f
            setPadding(0, 0, 0, halfPad / 2)
        }
        manualPanel.addView(envLabel)

        environmentSpinner = Spinner(this).apply {
            adapter = ArrayAdapter(
                this@ProvisioningActivity,
                android.R.layout.simple_spinner_dropdown_item,
                CloudEnvironments.displayNames,
            )
        }
        environmentSpinner.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                val envKey = CloudEnvironments.keys[position]
                val url = CloudEnvironments.resolve(envKey)
                if (url != null) {
                    cloudUrlInput.setText(url)
                    cloudUrlInput.isEnabled = false
                } else {
                    cloudUrlInput.setText("")
                    cloudUrlInput.isEnabled = true
                }
            }
            override fun onNothingSelected(parent: AdapterView<*>?) {}
        }
        manualPanel.addView(environmentSpinner)

        val cloudUrlLabel = TextView(this).apply {
            text = "Cloud URL"
            textSize = 14f
            setPadding(0, halfPad, 0, halfPad / 2)
        }
        manualPanel.addView(cloudUrlLabel)

        cloudUrlInput = EditText(this).apply {
            hint = "https://api.fccmiddleware.io"
            setText("https://api.fccmiddleware.io")
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            setSingleLine()
            isEnabled = false
            setPadding(halfPad, halfPad, halfPad, halfPad)
        }
        manualPanel.addView(cloudUrlInput)

        val siteCodeLabel = TextView(this).apply {
            text = "Site Code"
            textSize = 14f
            setPadding(0, halfPad, 0, halfPad / 2)
        }
        manualPanel.addView(siteCodeLabel)

        siteCodeInput = EditText(this).apply {
            hint = "e.g., SITE-001"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS
            setSingleLine()
            setPadding(halfPad, halfPad, halfPad, halfPad)
        }
        manualPanel.addView(siteCodeInput)

        val tokenLabel = TextView(this).apply {
            text = "Provisioning Token"
            textSize = 14f
            setPadding(0, halfPad, 0, halfPad / 2)
        }
        manualPanel.addView(tokenLabel)

        tokenInput = EditText(this).apply {
            hint = "Paste the one-time token from the admin portal"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            setSingleLine()
            setPadding(halfPad, halfPad, halfPad, halfPad)
        }
        manualPanel.addView(tokenInput)

        val manualButtonRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            setPadding(0, padding, 0, 0)
        }

        manualBackButton = Button(this).apply {
            text = "Back"
            textSize = 16f
        }
        manualButtonRow.addView(manualBackButton)

        val spacer = View(this).apply {
            layoutParams = LinearLayout.LayoutParams(padding, 0)
        }
        manualButtonRow.addView(spacer)

        manualRegisterButton = Button(this).apply {
            text = "Register"
            textSize = 16f
        }
        manualButtonRow.addView(manualRegisterButton)

        manualPanel.addView(manualButtonRow)

        root.addView(manualPanel)

        // ── Shared status elements ──────────────────────────────────
        progressBar = ProgressBar(this).apply {
            visibility = View.GONE
            setPadding(0, padding, 0, 0)
        }
        root.addView(progressBar)

        statusText = TextView(this).apply {
            visibility = View.GONE
            textSize = 14f
            gravity = Gravity.CENTER
            setPadding(0, padding, 0, 0)
        }
        root.addView(statusText)

        errorText = TextView(this).apply {
            visibility = View.GONE
            textSize = 14f
            gravity = Gravity.CENTER
            setTextColor(0xFFCC0000.toInt())
            setPadding(0, padding, 0, 0)
        }
        root.addView(errorText)

        val scrollView = ScrollView(this)
        scrollView.addView(root)
        return scrollView
    }
}

/** Parsed QR code bootstrap data. */
data class QrBootstrapData(
    val siteCode: String,
    val cloudBaseUrl: String,
    @Sensitive val provisioningToken: String,
    val environment: String? = null,
) {
    // S-007: redact the token so it can never appear in a log line even if the
    // object is accidentally passed to AppLogger or another logging call.
    override fun toString(): String =
        "QrBootstrapData(siteCode=$siteCode, cloudBaseUrl=$cloudBaseUrl, " +
            "provisioningToken=***, environment=$environment)"
}
