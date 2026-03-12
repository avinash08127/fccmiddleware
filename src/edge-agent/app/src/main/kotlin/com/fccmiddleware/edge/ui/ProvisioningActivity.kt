package com.fccmiddleware.edge.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.service.EdgeAgentForegroundService
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudRegistrationResult
import com.fccmiddleware.edge.sync.DeviceRegistrationRequest
import com.fccmiddleware.edge.sync.KeystoreDeviceTokenProvider
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanIntentResult
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonPrimitive
import org.koin.android.ext.android.inject
import java.time.Instant

/**
 * ProvisioningActivity — QR code scanner and device registration flow.
 *
 * Shown on first launch (or after factory reset) when no registration exists.
 * Flow:
 *   1. User taps "Scan QR Code"
 *   2. Camera opens and scans QR containing { v, sc, cu, pt }
 *   3. Extracts bootstrap data (siteCode, cloudBaseUrl, provisioningToken)
 *   4. Collects device fingerprint (serial, model, OS, app version)
 *   5. Calls POST /api/v1/agent/register
 *   6. On success: stores tokens in Keystore, identity in EncryptedPrefs, config in Room
 *   7. Starts EdgeAgentForegroundService
 *   8. Navigates to DiagnosticsActivity
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

    private val activityScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
    }

    // UI elements
    private lateinit var statusText: TextView
    private lateinit var errorText: TextView
    private lateinit var scanButton: Button
    private lateinit var progressBar: ProgressBar

    // QR scanner launcher
    private val scanLauncher = registerForActivityResult(ScanContract()) { result ->
        onScanResult(result)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(buildLayout())

        scanButton.setOnClickListener { startQrScan() }
    }

    override fun onDestroy() {
        activityScope.cancel()
        super.onDestroy()
    }

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
            showError("Camera permission is required to scan provisioning QR codes")
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
            showError("QR scan cancelled")
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

    private fun performRegistration(qrData: QrBootstrapData) {
        showProgress("Registering device with cloud...")
        scanButton.isEnabled = false

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
                    }
                    is CloudRegistrationResult.TransportError -> {
                        showError("Network error: ${result.message}. Check connectivity and try again.")
                        scanButton.isEnabled = true
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "Registration failed", e)
                showError("Registration failed: ${e.message}")
                scanButton.isEnabled = true
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun buildRegistrationRequest(qrData: QrBootstrapData): DeviceRegistrationRequest {
        val serialNumber = try {
            Build.getSerial()
        } catch (_: SecurityException) {
            Build.SERIAL
        }

        return DeviceRegistrationRequest(
            provisioningToken = qrData.provisioningToken,
            siteCode = qrData.siteCode,
            deviceSerialNumber = serialNumber,
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
            // 0. Update the singleton CloudApiClient base URL so all post-registration
            //    calls (upload, config poll, telemetry, token refresh) use the real endpoint
            //    instead of the "https://not-yet-provisioned" stub.
            cloudApiClient.updateBaseUrl(qrData.cloudBaseUrl)

            // 1. Store tokens in Android Keystore
            val tokenProvider = KeystoreDeviceTokenProvider(keystoreManager, encryptedPrefs, cloudApiClient)
            tokenProvider.storeTokens(response.deviceToken, response.refreshToken)

            // 2. Store identity in EncryptedSharedPreferences
            encryptedPrefs.saveRegistration(
                deviceId = response.deviceId,
                siteCode = response.siteCode,
                legalEntityId = response.legalEntityId,
                cloudBaseUrl = qrData.cloudBaseUrl,
            )

            // 3. Store FCC connection info from siteConfig if available
            response.siteConfig?.let { config ->
                try {
                    val fccConn = config["fccConnection"] as? JsonObject
                    fccConn?.get("host")?.jsonPrimitive?.content?.let { encryptedPrefs.fccHost = it }
                    fccConn?.get("port")?.jsonPrimitive?.int?.let { encryptedPrefs.fccPort = it }
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to extract FCC connection from siteConfig", e)
                }
            }

            // 4. Store initial config in AgentConfig table if siteConfig provided
            response.siteConfig?.let { siteConfig ->
                try {
                    val rawConfigJson = json.encodeToString(JsonObject.serializer(), siteConfig)
                    val entity = AgentConfig(
                        configJson = rawConfigJson,
                        configVersion = 1,
                        schemaVersion = 2,
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

    private fun buildLayout(): View {
        val padding = (16 * resources.displayMetrics.density).toInt()

        val layout = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(padding, padding, padding, padding)
        }

        val title = TextView(this).apply {
            text = "FCC Edge Agent Provisioning"
            textSize = 22f
            gravity = Gravity.CENTER
            setPadding(0, 0, 0, padding)
        }
        layout.addView(title)

        val description = TextView(this).apply {
            text = "Scan the provisioning QR code from the management portal to register this device."
            textSize = 16f
            gravity = Gravity.CENTER
            setPadding(0, 0, 0, padding * 2)
        }
        layout.addView(description)

        scanButton = Button(this).apply {
            text = "Scan QR Code"
            textSize = 18f
        }
        layout.addView(scanButton)

        progressBar = ProgressBar(this).apply {
            visibility = View.GONE
            setPadding(0, padding, 0, 0)
        }
        layout.addView(progressBar)

        statusText = TextView(this).apply {
            visibility = View.GONE
            textSize = 14f
            gravity = Gravity.CENTER
            setPadding(0, padding, 0, 0)
        }
        layout.addView(statusText)

        errorText = TextView(this).apply {
            visibility = View.GONE
            textSize = 14f
            gravity = Gravity.CENTER
            setTextColor(0xFFCC0000.toInt())
            setPadding(0, padding, 0, 0)
        }
        layout.addView(errorText)

        val scrollView = ScrollView(this)
        scrollView.addView(layout)
        return scrollView
    }
}

/** Parsed QR code bootstrap data. */
data class QrBootstrapData(
    val siteCode: String,
    val cloudBaseUrl: String,
    val provisioningToken: String,
)
