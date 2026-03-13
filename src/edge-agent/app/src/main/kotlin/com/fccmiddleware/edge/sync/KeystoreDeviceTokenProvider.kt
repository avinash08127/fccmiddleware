package com.fccmiddleware.edge.sync

import android.util.Base64
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

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

    /**
     * Volatile in-memory cache of the decommission flag.
     * Eliminates the race window where one worker detects 403 DEVICE_DECOMMISSIONED
     * but other workers pass their isDecommissioned() check before SharedPreferences
     * is updated. Once set to true, all subsequent isDecommissioned() calls return
     * true immediately without hitting SharedPreferences.
     */
    @Volatile
    private var decommissionedCached: Boolean = false

    /**
     * Volatile in-memory cache of the re-provisioning flag.
     * Same pattern as [decommissionedCached] — eliminates race window between
     * detection and SharedPreferences update.
     */
    @Volatile
    private var reprovisioningCached: Boolean = false

    /**
     * Mutex serializing [refreshAccessToken] calls so that concurrent 401
     * handlers (CloudUploadWorker, ConfigPollWorker, PreAuthCloudForwardWorker)
     * do not race to issue duplicate refresh requests. The first caller performs
     * the refresh; others suspend and then see the already-refreshed token.
     */
    private val refreshMutex = Mutex()

    override fun getAccessToken(): String? {
        if (decommissionedCached || encryptedPrefs.isDecommissioned) return null

        val blob = encryptedPrefs.getDeviceTokenBlob() ?: return null
        return try {
            val encrypted = Base64.decode(blob, Base64.NO_WRAP)
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_DEVICE_JWT, encrypted)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to retrieve access token from Keystore", e)
            null
        }
    }

    override fun getLegalEntityId(): String? {
        return encryptedPrefs.legalEntityId
    }

    override suspend fun refreshAccessToken(): Boolean = refreshMutex.withLock {
        // Inside the mutex: if another caller already refreshed while we were
        // waiting, the token stored in EncryptedPrefs is now fresh. We detect
        // this by checking that we still hold a valid refresh token and proceed.
        // This is safe because storeTokens() atomically rotates both tokens.

        if (decommissionedCached || encryptedPrefs.isDecommissioned) {
            AppLogger.w(TAG, "Device is decommissioned — cannot refresh token")
            return@withLock false
        }

        val client = cloudApiClient ?: run {
            AppLogger.w(TAG, "CloudApiClient not available — cannot refresh token")
            return@withLock false
        }

        val currentRefreshToken = getRefreshToken() ?: run {
            AppLogger.w(TAG, "No refresh token available — cannot refresh")
            return@withLock false
        }

        // FM-S03: Include the current (even expired) device JWT so the cloud can
        // verify identity binding between the refresh token and the device.
        val currentDeviceToken = getAccessTokenRaw() ?: run {
            AppLogger.w(TAG, "No device token available — cannot refresh (FM-S03 requires JWT)")
            return@withLock false
        }

        when (val result = client.refreshToken(currentRefreshToken, currentDeviceToken)) {
            is CloudTokenRefreshResult.Success -> {
                val stored = storeTokens(result.response.deviceToken, result.response.refreshToken)
                if (!stored) {
                    AppLogger.e(TAG, "Token refresh succeeded but failed to store new tokens in Keystore")
                    return@withLock false
                }
                AppLogger.i(TAG, "Token refreshed successfully, expires=${result.response.tokenExpiresAt}")
                true
            }
            is CloudTokenRefreshResult.Unauthorized -> {
                AppLogger.w(TAG, "Refresh token expired or revoked — re-provisioning required")
                markReprovisioningRequired()
                false
            }
            is CloudTokenRefreshResult.Forbidden -> {
                if (result.errorCode == "DEVICE_DECOMMISSIONED") {
                    markDecommissioned()
                }
                AppLogger.w(TAG, "Token refresh forbidden: ${result.errorCode}")
                false
            }
            is CloudTokenRefreshResult.TransportError -> {
                AppLogger.w(TAG, "Token refresh transport error: ${result.message}")
                false
            }
        }
    }

    override fun isDecommissioned(): Boolean {
        // Fast-path: volatile in-memory flag avoids SharedPreferences I/O
        if (decommissionedCached) return true
        val persisted = encryptedPrefs.isDecommissioned
        if (persisted) decommissionedCached = true
        return persisted
    }

    override fun markDecommissioned() {
        // Set volatile flag FIRST so concurrent workers see it immediately,
        // then persist to SharedPreferences for crash recovery.
        decommissionedCached = true
        encryptedPrefs.isDecommissioned = true
        AppLogger.w(TAG, "Device marked as decommissioned — all sync stopped")
    }

    override fun isReprovisioningRequired(): Boolean {
        if (reprovisioningCached) return true
        val persisted = encryptedPrefs.isReprovisioningRequired
        if (persisted) reprovisioningCached = true
        return persisted
    }

    override fun markReprovisioningRequired() {
        // Set volatile flag FIRST for immediate visibility, then persist.
        reprovisioningCached = true
        // M-03: Write both flags in a single atomic commit() to prevent a partial
        // state where isReprovisioningRequired=true but isRegistered is still true
        // (if the process is killed between two separate writes).
        encryptedPrefs.setReprovisioningAndUnregister()
        // H-02: Clear old Keystore keys — the tokens are expired/revoked and should not
        // remain on disk. Prevents stale identity data from persisting across re-provisioning.
        keystoreManager.clearAll()
        AppLogger.w(TAG, "Device marked as requiring re-provisioning — refresh token expired, keys cleared")
    }

    /**
     * Store both tokens securely after registration or refresh.
     * Encrypts each token with its dedicated Keystore key and persists the
     * Base64-encoded ciphertext in EncryptedSharedPreferences.
     */
    override fun storeTokens(deviceToken: String, refreshToken: String): Boolean {
        val deviceEncrypted = keystoreManager.storeSecret(KeystoreManager.ALIAS_DEVICE_JWT, deviceToken)
        if (deviceEncrypted != null) {
            encryptedPrefs.storeDeviceTokenBlob(Base64.encodeToString(deviceEncrypted, Base64.NO_WRAP))
        } else {
            AppLogger.e(TAG, "Failed to encrypt device token — Keystore may be unavailable")
            return false
        }

        val refreshEncrypted = keystoreManager.storeSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, refreshToken)
        if (refreshEncrypted != null) {
            encryptedPrefs.storeRefreshTokenBlob(Base64.encodeToString(refreshEncrypted, Base64.NO_WRAP))
        } else {
            AppLogger.e(TAG, "Failed to encrypt refresh token — Keystore may be unavailable")
            return false
        }

        return true
    }

    /**
     * FM-S03: Retrieves the current device JWT (may be expired) for identity
     * binding during token refresh. Unlike [getAccessToken], this does NOT
     * return null when the device is decommissioned — the token is needed
     * for the refresh request even if it will be rejected.
     */
    private fun getAccessTokenRaw(): String? {
        val blob = encryptedPrefs.getDeviceTokenBlob() ?: return null
        return try {
            val encrypted = Base64.decode(blob, Base64.NO_WRAP)
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_DEVICE_JWT, encrypted)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to retrieve device token for refresh identity binding", e)
            null
        }
    }

    private fun getRefreshToken(): String? {
        val blob = encryptedPrefs.getRefreshTokenBlob() ?: return null
        return try {
            val encrypted = Base64.decode(blob, Base64.NO_WRAP)
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, encrypted)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to retrieve refresh token from Keystore", e)
            null
        }
    }
}
