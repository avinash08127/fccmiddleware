package com.fccmiddleware.edge.ui

import android.app.Application
import android.os.Build
import android.provider.Settings
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.registration.RegistrationHandler
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudRegistrationResult
import com.fccmiddleware.edge.sync.DeviceRegistrationRequest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

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
    private val encryptedPrefs: EncryptedPrefsManager,
    private val registrationHandler: RegistrationHandler,
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
        return DeviceRegistrationRequest(
            provisioningToken = qrData.provisioningToken,
            siteCode = qrData.siteCode,
            deviceSerialNumber = resolveDeviceSerialNumber(androidId),
            deviceModel = Build.MODEL,
            osVersion = Build.VERSION.RELEASE,
            agentVersion = ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName ?: "1.0.0",
            deviceClass = "ANDROID",
            roleCapability = "PRIMARY_ELIGIBLE",
            capabilities = listOf(
                "FCC_CONTROL",
                "LOCALHOST_API",
                "LAN_PROXY",
                "TRANSACTION_BUFFER",
                "TELEMETRY",
            ),
            peerApi = com.fccmiddleware.edge.sync.PeerApiRegistrationMetadata(
                port = 8585,
                tlsEnabled = false,
            ),
        )
    }

    internal fun resolveDeviceSerialNumber(androidId: String?): String =
        androidId?.takeUnless { it.isBlank() }
            ?: encryptedPrefs.getOrCreateProvisioningDeviceSerialFallback()

    /**
     * AT-014: Delegates to [RegistrationHandler] for credential storage, config
     * encryption, and Room persistence. The ViewModel only manages UI state.
     */
    private suspend fun handleRegistrationSuccess(
        qrData: QrBootstrapData,
        result: CloudRegistrationResult.Success,
    ) {
        _registrationState.value = RegistrationState.InProgress("Storing credentials securely...")

        try {
            withContext(Dispatchers.IO) {
                registrationHandler.completeRegistration(
                    qrCloudBaseUrl = qrData.cloudBaseUrl,
                    environment = qrData.environment,
                    response = result.response,
                )
            }

            _registrationState.value = RegistrationState.Success
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to store registration data", e)
            _registrationState.value = RegistrationState.Error("Registration failed: ${e.message}")
        }
    }
}
