package com.fccmiddleware.edge.sync

import android.util.Base64
import android.util.Log
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager

/**
 * Production [DeviceTokenProvider] backed by Android Keystore + EncryptedSharedPreferences.
 *
 * Token storage strategy (per security spec §5.1):
 *   - Device JWT and refresh token are encrypted with AES-256-GCM keys in the
 *     Android Keystore (hardware-backed TEE on Urovo i9100).
 *   - The encrypted blobs are persisted as Base64 strings in EncryptedSharedPreferences
 *     so they survive app restarts.
 *   - Plaintext tokens are never stored on disk or logged.
 *
 * Token refresh:
 *   - On 401 from any cloud API call, the caller invokes [refreshAccessToken].
 *   - This calls POST /api/v1/agent/token/refresh with the current refresh token.
 *   - On success, both tokens are rotated (new refresh token replaces old).
 *   - On failure, the device enters REGISTRATION_REQUIRED state.
 *
 * Decommission:
 *   - On 403 DEVICE_DECOMMISSIONED, [markDecommissioned] is called.
 *   - Once decommissioned, all token operations return null/false.
 */
class KeystoreDeviceTokenProvider(
    private val keystoreManager: KeystoreManager,
    private val encryptedPrefs: EncryptedPrefsManager,
    private val cloudApiClient: CloudApiClient?,
) : DeviceTokenProvider {

    companion object {
        private const val TAG = "DeviceTokenProvider"
    }

    override fun getAccessToken(): String? {
        if (encryptedPrefs.isDecommissioned) return null

        val blob = encryptedPrefs.getDeviceTokenBlob() ?: return null
        return try {
            val encrypted = Base64.decode(blob, Base64.NO_WRAP)
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_DEVICE_JWT, encrypted)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to retrieve access token from Keystore", e)
            null
        }
    }

    override fun getLegalEntityId(): String? {
        return encryptedPrefs.legalEntityId
    }

    override suspend fun refreshAccessToken(): Boolean {
        if (encryptedPrefs.isDecommissioned) {
            Log.w(TAG, "Device is decommissioned — cannot refresh token")
            return false
        }

        val client = cloudApiClient ?: run {
            Log.w(TAG, "CloudApiClient not available — cannot refresh token")
            return false
        }

        val currentRefreshToken = getRefreshToken() ?: run {
            Log.w(TAG, "No refresh token available — cannot refresh")
            return false
        }

        return when (val result = client.refreshToken(currentRefreshToken)) {
            is CloudTokenRefreshResult.Success -> {
                storeTokens(result.response.deviceToken, result.response.refreshToken)
                Log.i(TAG, "Token refreshed successfully, expires=${result.response.tokenExpiresAt}")
                true
            }
            is CloudTokenRefreshResult.Unauthorized -> {
                Log.w(TAG, "Refresh token expired or revoked — re-provisioning required")
                false
            }
            is CloudTokenRefreshResult.Forbidden -> {
                if (result.errorCode == "DEVICE_DECOMMISSIONED") {
                    markDecommissioned()
                }
                Log.w(TAG, "Token refresh forbidden: ${result.errorCode}")
                false
            }
            is CloudTokenRefreshResult.TransportError -> {
                Log.w(TAG, "Token refresh transport error: ${result.message}")
                false
            }
        }
    }

    override fun isDecommissioned(): Boolean {
        return encryptedPrefs.isDecommissioned
    }

    override fun markDecommissioned() {
        encryptedPrefs.isDecommissioned = true
        Log.w(TAG, "Device marked as decommissioned — all sync stopped")
    }

    /**
     * Store both tokens securely after registration or refresh.
     * Encrypts each token with its dedicated Keystore key and persists the
     * Base64-encoded ciphertext in EncryptedSharedPreferences.
     */
    fun storeTokens(deviceToken: String, refreshToken: String) {
        val deviceEncrypted = keystoreManager.storeSecret(KeystoreManager.ALIAS_DEVICE_JWT, deviceToken)
        if (deviceEncrypted != null) {
            encryptedPrefs.storeDeviceTokenBlob(Base64.encodeToString(deviceEncrypted, Base64.NO_WRAP))
        } else {
            Log.e(TAG, "Failed to encrypt device token")
        }

        val refreshEncrypted = keystoreManager.storeSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, refreshToken)
        if (refreshEncrypted != null) {
            encryptedPrefs.storeRefreshTokenBlob(Base64.encodeToString(refreshEncrypted, Base64.NO_WRAP))
        } else {
            Log.e(TAG, "Failed to encrypt refresh token")
        }
    }

    private fun getRefreshToken(): String? {
        val blob = encryptedPrefs.getRefreshTokenBlob() ?: return null
        return try {
            val encrypted = Base64.decode(blob, Base64.NO_WRAP)
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, encrypted)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to retrieve refresh token from Keystore", e)
            null
        }
    }
}
