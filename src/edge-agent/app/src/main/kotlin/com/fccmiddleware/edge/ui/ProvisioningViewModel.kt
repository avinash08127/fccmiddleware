package com.fccmiddleware.edge.ui

import android.app.Application
import android.os.Build
import android.provider.Settings
import android.util.Base64
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import com.fccmiddleware.edge.config.EdgeAgentConfigJson
import com.fccmiddleware.edge.config.SiteDataManager
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudRegistrationResult
import com.fccmiddleware.edge.sync.DeviceRegistrationRequest
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import java.time.Instant

/**
 * ProvisioningViewModel — owns the device registration lifecycle (T-003).
 *
 * Moving registration into a ViewModel means:
 *   - The in-flight coroutine runs in [viewModelScope], which survives activity
 *     recreation on rotation.  The new activity instance simply re-collects
 *     [registrationState] and picks up wherever the registration got to.
 *   - [registrationState] is a StateFlow, so the new activity instance
 *     immediately receives the current state without missing any transition.
 *   - The [isRegistering] guard is VM-scoped, so double-taps across rotation
 *     are still blocked.
 */
class ProvisioningViewModel(
    application: Application,
    private val cloudApiClient: CloudApiClient,
    private val keystoreManager: KeystoreManager,
    private val encryptedPrefs: EncryptedPrefsManager,
    private val agentConfigDao: AgentConfigDao,
    private val tokenProvider: DeviceTokenProvider,
    private val siteDataManager: SiteDataManager,
    private val bufferDatabase: BufferDatabase,
) : AndroidViewModel(application) {

    sealed interface RegistrationState {
        object Idle : RegistrationState
        data class InProgress(val message: String) : RegistrationState
        data class Error(val message: String) : RegistrationState
        /** Credentials stored; Activity should start service and navigate. */
        object Success : RegistrationState
    }

    private val _registrationState = MutableStateFlow<RegistrationState>(RegistrationState.Idle)
    val registrationState: StateFlow<RegistrationState> = _registrationState

    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    companion object {
        private const val TAG = "ProvisioningViewModel"
    }

    fun register(qrData: QrBootstrapData) {
        if (_registrationState.value is RegistrationState.InProgress) return
        _registrationState.value = RegistrationState.InProgress("Registering device with cloud...")

        viewModelScope.launch {
            try {
                val request = buildRegistrationRequest(qrData)
                val result = withContext(Dispatchers.IO) {
                    cloudApiClient.registerDevice(qrData.cloudBaseUrl, request)
                }
                when (result) {
                    is CloudRegistrationResult.Success ->
                        handleRegistrationSuccess(qrData, result)
                    is CloudRegistrationResult.Rejected ->
                        _registrationState.value = RegistrationState.Error(
                            "Registration rejected: ${result.errorCode} — ${result.message}"
                        )
                    is CloudRegistrationResult.TransportError ->
                        _registrationState.value = RegistrationState.Error(
                            "Network error: ${result.message}. Check connectivity and try again."
                        )
                }
            } catch (e: Exception) {
                AppLogger.e(TAG, "Registration failed", e)
                _registrationState.value = RegistrationState.Error("Registration failed: ${e.message}")
            }
        }
    }

    /** Call after the Activity has handled [RegistrationState.Success] navigation. */
    fun onNavigationComplete() {
        _registrationState.value = RegistrationState.Idle
    }

    private fun buildRegistrationRequest(qrData: QrBootstrapData): DeviceRegistrationRequest {
        val ctx = getApplication<Application>()
        // ANDROID_ID is app-scoped (unique per signing key + user + device) since Android 8.
        // It is NOT a hardware serial number — it changes on factory reset and differs per user
        // account. The backend must treat deviceSerialNumber as an app-scoped dedup key only,
        // not as a hardware identifier. Build.SERIAL was deprecated in API 29 and requires
        // READ_PRIVILEGED_PHONE_STATE, making ANDROID_ID the correct alternative for this use.
        val androidId = Settings.Secure.getString(ctx.contentResolver, Settings.Secure.ANDROID_ID)
            ?: "unknown-${java.util.UUID.randomUUID().toString().take(8)}"
        return DeviceRegistrationRequest(
            provisioningToken = qrData.provisioningToken,
            siteCode = qrData.siteCode,
            deviceSerialNumber = androidId,
            deviceModel = Build.MODEL,
            osVersion = Build.VERSION.RELEASE,
            agentVersion = ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName ?: "1.0.0",
        )
    }

    private suspend fun handleRegistrationSuccess(
        qrData: QrBootstrapData,
        result: CloudRegistrationResult.Success,
    ) {
        val response = result.response
        AppLogger.i(TAG, "Registration successful: deviceId=${response.deviceId}, site=${response.siteCode}")
        _registrationState.value = RegistrationState.InProgress("Storing credentials securely...")

        try {
            withContext(Dispatchers.IO) {
                // AF-013: Clear the Room database before credentials to prevent cross-site
                // data contamination when re-provisioning for a different site.
                bufferDatabase.clearAllData()

                // Clear stale Keystore keys and EncryptedPrefs from any previous registration.
                keystoreManager.clearAll()
                encryptedPrefs.clearAll()

                val parsedSiteConfig = response.siteConfig?.let { siteConfig ->
                    runCatching {
                        EdgeAgentConfigJson.decode(
                            json.encodeToString(JsonObject.serializer(), siteConfig),
                        )
                    }.onFailure { e ->
                        AppLogger.e(TAG, "Failed to parse registration siteConfig against canonical contract", e)
                    }.getOrNull()
                }

                val effectiveCloudBaseUrl = parsedSiteConfig?.sync?.cloudBaseUrl ?: qrData.cloudBaseUrl
                cloudApiClient.updateBaseUrl(effectiveCloudBaseUrl)

                val tokensStored = tokenProvider.storeTokens(response.deviceToken, response.refreshToken)
                if (!tokensStored) {
                    throw IllegalStateException(
                        "Failed to store device credentials in Android Keystore. " +
                        "The device cannot authenticate with the cloud without stored tokens. " +
                        "Please try again or clear app data and re-provision."
                    )
                }

                encryptedPrefs.saveRegistration(
                    deviceId = response.deviceId,
                    siteCode = response.siteCode,
                    legalEntityId = response.legalEntityId,
                    cloudBaseUrl = effectiveCloudBaseUrl,
                    environment = qrData.environment,
                )

                parsedSiteConfig?.fcc?.hostAddress?.let { encryptedPrefs.fccHost = it }
                parsedSiteConfig?.fcc?.port?.let { encryptedPrefs.fccPort = it }

                parsedSiteConfig?.let { siteConfig ->
                    val rawConfigJson = EdgeAgentConfigJson.encode(siteConfig)
                    val encryptedBytes = keystoreManager.storeSecret(
                        KeystoreManager.ALIAS_CONFIG_INTEGRITY, rawConfigJson
                    )
                    val persistedJson = if (encryptedBytes != null) {
                        "ENC:" + Base64.encodeToString(encryptedBytes, Base64.NO_WRAP)
                    } else {
                        AppLogger.w(TAG, "Config encryption failed — persisting raw JSON")
                        rawConfigJson
                    }
                    val entity = AgentConfig(
                        configJson = persistedJson,
                        configVersion = siteConfig.configVersion,
                        schemaVersion = siteConfig.schemaVersion.substringBefore(".").toIntOrNull() ?: 1,
                        receivedAt = Instant.now().toString(),
                    )

                    var writeSucceeded = false
                    for (attempt in 1..2) {
                        try {
                            agentConfigDao.upsert(entity)
                            val stored = agentConfigDao.get()
                            if (stored != null && stored.configVersion == siteConfig.configVersion) {
                                writeSucceeded = true
                                AppLogger.i(TAG, "Initial config stored in Room (encrypted, attempt=$attempt)")
                                break
                            } else {
                                AppLogger.w(TAG, "Config write verification failed (attempt=$attempt)")
                            }
                        } catch (e: Exception) {
                            AppLogger.e(TAG, "Failed to store initial config (attempt=$attempt)", e)
                            if (attempt < 2) kotlinx.coroutines.delay(200)
                        }
                    }
                    if (!writeSucceeded) {
                        AppLogger.e(TAG, "Initial config could not be persisted after retries — service will fetch on first poll")
                    }
                }

                parsedSiteConfig?.let { config ->
                    try {
                        siteDataManager.syncFromConfig(config)
                    } catch (e: Exception) {
                        AppLogger.e(TAG, "Failed to sync site data — will populate on first config poll", e)
                    }
                }
            }

            _registrationState.value = RegistrationState.Success
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to store registration data", e)
            _registrationState.value = RegistrationState.Error("Registration failed: ${e.message}")
        }
    }
}
