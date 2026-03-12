package com.fccmiddleware.edge.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import android.os.Bundle
import android.text.InputType
import android.util.Log
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import com.fccmiddleware.edge.config.EdgeAgentConfigJson
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudRegistrationResult
import com.fccmiddleware.edge.sync.DeviceRegistrationRequest
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanIntentResult
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonPrimitive
import org.koin.android.ext.android.inject
import java.time.Instant

/**
 * ProvisioningActivity — QR code scanner and manual entry device registration flow.
 *
 * Shown on first launch (or after factory reset) when no registration exists.
 * Two provisioning paths:
 *   A. QR code scan (preferred): camera scans QR containing { v, sc, cu, pt }
 *   B. Manual entry (fallback): user types Cloud URL, Site Code, and Provisioning Token
 * Both paths then:
 *   1. Collect device fingerprint (serial, model, OS, app version)
 *   2. Call POST /api/v1/agent/register
 *   3. On success: store tokens in Keystore, identity in EncryptedPrefs, config in Room
 *   4. Start EdgeAgentForegroundService
 *   5. Navigate to DiagnosticsActivity
 */
class ProvisioningActivity : AppCompatActivity() {

    companion object {
        private const val TAG = "ProvisioningActivity"
        private const val CAMERA_PERMISSION_REQUEST = 100
    }

    private val encryptedPrefs: EncryptedPrefsManager by inject()
    private val keystoreManager: KeystoreManager by inject()
    private val cloudApiClient: CloudApiClient by inject()
    private val agentConfigDao: AgentConfigDao by inject()
    private val tokenProvider: DeviceTokenProvider by inject()

    private val activityScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

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

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        scanButton.setOnClickListener { startQrScan() }
        manualEntryButton.setOnClickListener { showManualEntryScreen() }
        manualBackButton.setOnClickListener { showMethodSelectionScreen() }
        manualRegisterButton.setOnClickListener { submitManualEntry() }
    }

    override fun onDestroy() {
        activityScope.cancel()
        super.onDestroy()
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

        Log.i(TAG, "QR code scanned, parsing payload")
        val qrData = parseQrPayload(contents)
        if (qrData == null) {
            showError("Invalid QR code format. Expected provisioning QR with v, sc, cu, pt fields.")
            return
        }

        performRegistration(qrData)
    }

    // ── Manual entry path ────────────────────────────────────────────────────

    private fun submitManualEntry() {
        val cloudUrl = cloudUrlInput.text.toString().trim()
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

        Log.i(TAG, "Manual entry submitted, starting registration")
        val bootstrapData = QrBootstrapData(
            siteCode = siteCode,
            cloudBaseUrl = cloudUrl.trimEnd('/'),
            provisioningToken = token,
        )
        performRegistration(bootstrapData)
    }

    // ── QR payload parsing ───────────────────────────────────────────────────

    /**
     * Parse the QR code JSON payload.
     * Expected format: { "v": 1, "sc": "SITE-CODE", "cu": "https://...", "pt": "token" }
     */
    internal fun parseQrPayload(rawJson: String): QrBootstrapData? {
        return try {
            val obj = json.decodeFromString<JsonObject>(rawJson)
            val version = obj["v"]?.jsonPrimitive?.int
            val siteCode = obj["sc"]?.jsonPrimitive?.content
            val cloudUrl = obj["cu"]?.jsonPrimitive?.content
            val token = obj["pt"]?.jsonPrimitive?.content

            if (version == null || version != 1) {
                Log.w(TAG, "Unsupported QR version: $version")
                return null
            }
            if (siteCode.isNullOrBlank() || cloudUrl.isNullOrBlank() || token.isNullOrBlank()) {
                Log.w(TAG, "Missing required QR fields")
                return null
            }

            if (!cloudUrl.startsWith("https://")) {
                Log.w(TAG, "Cloud URL must use HTTPS — rejecting insecure QR code")
                return null
            }

            QrBootstrapData(
                siteCode = siteCode,
                cloudBaseUrl = cloudUrl.trimEnd('/'),
                provisioningToken = token,
            )
        } catch (e: Exception) {
            Log.e(TAG, "Failed to parse QR payload", e)
            null
        }
    }

    // ── Cloud registration (shared by both paths) ────────────────────────────

    private fun performRegistration(qrData: QrBootstrapData) {
        showProgress("Registering device with cloud...")
        scanButton.isEnabled = false
        manualRegisterButton.isEnabled = false
        manualBackButton.isEnabled = false

        activityScope.launch {
            try {
                val request = buildRegistrationRequest(qrData)
                val result = withContext(Dispatchers.IO) {
                    cloudApiClient.registerDevice(qrData.cloudBaseUrl, request)
                }

                when (result) {
                    is CloudRegistrationResult.Success -> {
                        handleRegistrationSuccess(qrData, result)
                    }
                    is CloudRegistrationResult.Rejected -> {
                        showError("Registration rejected: ${result.errorCode} — ${result.message}")
                        scanButton.isEnabled = true
                        manualRegisterButton.isEnabled = true
                        manualBackButton.isEnabled = true
                    }
                    is CloudRegistrationResult.TransportError -> {
                        showError("Network error: ${result.message}. Check connectivity and try again.")
                        scanButton.isEnabled = true
                        manualRegisterButton.isEnabled = true
                        manualBackButton.isEnabled = true
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "Registration failed", e)
                showError("Registration failed: ${e.message}")
                scanButton.isEnabled = true
                manualRegisterButton.isEnabled = true
                manualBackButton.isEnabled = true
            }
        }
    }

    private fun buildRegistrationRequest(qrData: QrBootstrapData): DeviceRegistrationRequest {
        val androidId = Settings.Secure.getString(contentResolver, Settings.Secure.ANDROID_ID)
            ?: "unknown-${java.util.UUID.randomUUID().toString().take(8)}"

        return DeviceRegistrationRequest(
            provisioningToken = qrData.provisioningToken,
            siteCode = qrData.siteCode,
            deviceSerialNumber = androidId,
            deviceModel = Build.MODEL,
            osVersion = Build.VERSION.RELEASE,
            agentVersion = packageManager.getPackageInfo(packageName, 0).versionName ?: "1.0.0",
        )
    }

    private suspend fun handleRegistrationSuccess(
        qrData: QrBootstrapData,
        result: CloudRegistrationResult.Success,
    ) {
        val response = result.response
        Log.i(TAG, "Registration successful: deviceId=${response.deviceId}, site=${response.siteCode}")

        showProgress("Storing credentials securely...")

        withContext(Dispatchers.IO) {
            val parsedSiteConfig = response.siteConfig?.let { siteConfig ->
                runCatching {
                    EdgeAgentConfigJson.decode(
                        json.encodeToString(JsonObject.serializer(), siteConfig),
                    )
                }.onFailure { e ->
                    Log.w(TAG, "Failed to parse registration siteConfig against canonical contract", e)
                }.getOrNull()
            }

            val effectiveCloudBaseUrl = parsedSiteConfig?.sync?.cloudBaseUrl ?: qrData.cloudBaseUrl
            cloudApiClient.updateBaseUrl(effectiveCloudBaseUrl)

            // 1. Store tokens in Android Keystore (use DI singleton to keep in-memory state consistent)
            tokenProvider.storeTokens(response.deviceToken, response.refreshToken)

            // 2. Store identity in EncryptedSharedPreferences
            encryptedPrefs.saveRegistration(
                deviceId = response.deviceId,
                siteCode = response.siteCode,
                legalEntityId = response.legalEntityId,
                cloudBaseUrl = effectiveCloudBaseUrl,
            )

            // 3. Persist bootstrap FCC host/port for diagnostics before the service starts.
            parsedSiteConfig?.fcc?.hostAddress?.let { encryptedPrefs.fccHost = it }
            parsedSiteConfig?.fcc?.port?.let { encryptedPrefs.fccPort = it }

            // 4. Store initial config in AgentConfig table if siteConfig provided
            parsedSiteConfig?.let { siteConfig ->
                try {
                    val rawConfigJson = EdgeAgentConfigJson.encode(siteConfig)
                    val entity = AgentConfig(
                        configJson = rawConfigJson,
                        configVersion = siteConfig.configVersion,
                        schemaVersion = siteConfig.schemaVersion.substringBefore(".").toIntOrNull() ?: 1,
                        receivedAt = Instant.now().toString(),
                    )
                    agentConfigDao.upsert(entity)
                    Log.i(TAG, "Initial config stored in Room")
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to store initial config — will fetch on first poll", e)
                }
            }
        }

        showProgress("Starting Edge Agent service...")

        // 5. Start the foreground service
        val serviceIntent = Intent(this@ProvisioningActivity, EdgeAgentForegroundService::class.java)
        startForegroundService(serviceIntent)

        // 6. Navigate to diagnostics
        val intent = Intent(this@ProvisioningActivity, DiagnosticsActivity::class.java)
        intent.flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TASK
        startActivity(intent)
        finish()
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
            text = "FCC Edge Agent Provisioning"
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

        val cloudUrlLabel = TextView(this).apply {
            text = "Cloud URL"
            textSize = 14f
            setPadding(0, 0, 0, halfPad / 2)
        }
        manualPanel.addView(cloudUrlLabel)

        cloudUrlInput = EditText(this).apply {
            hint = "https://api.fccmiddleware.io"
            setText("https://api.fccmiddleware.io")
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            setSingleLine()
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
    val provisioningToken: String,
)
