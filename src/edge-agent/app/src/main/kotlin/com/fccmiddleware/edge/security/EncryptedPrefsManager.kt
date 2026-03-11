package com.fccmiddleware.edge.security

import android.content.Context
import android.content.SharedPreferences
import android.util.Log
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKeys

/**
 * EncryptedPrefsManager — wraps EncryptedSharedPreferences for secure storage
 * of device registration identity and site binding.
 *
 * Stored keys (per security spec):
 *   - device_id         — UUID assigned by cloud at registration
 *   - site_code         — Site this device is bound to
 *   - legal_entity_id   — Tenant ID (prevents cross-tenant leakage)
 *   - cloud_base_url    — Cloud API base URL (prevents redirect attacks)
 *   - fcc_host          — FCC controller LAN address
 *   - fcc_port          — FCC controller port
 *   - is_registered     — Boolean flag for fast provisioning check
 *   - is_decommissioned — Boolean flag for decommission state
 *
 * Non-sensitive config (poll intervals, batch sizes, log level) stays in
 * regular SharedPreferences via ConfigManager/Room.
 *
 * NEVER log token values, credentials, or sensitive identity fields.
 */
class EncryptedPrefsManager(context: Context) {

    companion object {
        private const val TAG = "EncryptedPrefsManager"
        private const val PREFS_FILE = "fcc_edge_secure_prefs"

        const val KEY_DEVICE_ID = "device_id"
        const val KEY_SITE_CODE = "site_code"
        const val KEY_LEGAL_ENTITY_ID = "legal_entity_id"
        const val KEY_CLOUD_BASE_URL = "cloud_base_url"
        const val KEY_FCC_HOST = "fcc_host"
        const val KEY_FCC_PORT = "fcc_port"
        const val KEY_IS_REGISTERED = "is_registered"
        const val KEY_IS_DECOMMISSIONED = "is_decommissioned"
        const val KEY_DEVICE_TOKEN_ENCRYPTED = "device_token_enc"
        const val KEY_REFRESH_TOKEN_ENCRYPTED = "refresh_token_enc"
    }

    // No fallback to regular SharedPreferences — storing sensitive identity data
    // (device tokens, site binding) unencrypted is a security violation.
    // If EncryptedSharedPreferences fails, the agent cannot operate safely.
    private val prefs: SharedPreferences = run {
        val masterKeyAlias = MasterKeys.getOrCreate(MasterKeys.AES256_GCM_SPEC)
        EncryptedSharedPreferences.create(
            PREFS_FILE,
            masterKeyAlias,
            context,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    }

    // ---- Registration identity ----

    var deviceId: String?
        get() = prefs.getString(KEY_DEVICE_ID, null)
        set(value) = prefs.edit().putString(KEY_DEVICE_ID, value).apply()

    var siteCode: String?
        get() = prefs.getString(KEY_SITE_CODE, null)
        set(value) = prefs.edit().putString(KEY_SITE_CODE, value).apply()

    var legalEntityId: String?
        get() = prefs.getString(KEY_LEGAL_ENTITY_ID, null)
        set(value) = prefs.edit().putString(KEY_LEGAL_ENTITY_ID, value).apply()

    var cloudBaseUrl: String?
        get() = prefs.getString(KEY_CLOUD_BASE_URL, null)
        set(value) = prefs.edit().putString(KEY_CLOUD_BASE_URL, value).apply()

    var fccHost: String?
        get() = prefs.getString(KEY_FCC_HOST, null)
        set(value) = prefs.edit().putString(KEY_FCC_HOST, value).apply()

    var fccPort: Int
        get() = prefs.getInt(KEY_FCC_PORT, 0)
        set(value) = prefs.edit().putInt(KEY_FCC_PORT, value).apply()

    var isRegistered: Boolean
        get() = prefs.getBoolean(KEY_IS_REGISTERED, false)
        set(value) = prefs.edit().putBoolean(KEY_IS_REGISTERED, value).apply()

    var isDecommissioned: Boolean
        get() = prefs.getBoolean(KEY_IS_DECOMMISSIONED, false)
        set(value) = prefs.edit().putBoolean(KEY_IS_DECOMMISSIONED, value).apply()

    // ---- Encrypted token blobs (Keystore-encrypted, stored as Base64) ----

    fun storeDeviceTokenBlob(encoded: String) {
        prefs.edit().putString(KEY_DEVICE_TOKEN_ENCRYPTED, encoded).apply()
    }

    fun getDeviceTokenBlob(): String? {
        return prefs.getString(KEY_DEVICE_TOKEN_ENCRYPTED, null)
    }

    fun storeRefreshTokenBlob(encoded: String) {
        prefs.edit().putString(KEY_REFRESH_TOKEN_ENCRYPTED, encoded).apply()
    }

    fun getRefreshTokenBlob(): String? {
        return prefs.getString(KEY_REFRESH_TOKEN_ENCRYPTED, null)
    }

    /**
     * Persist all registration data atomically after a successful registration.
     */
    fun saveRegistration(
        deviceId: String,
        siteCode: String,
        legalEntityId: String,
        cloudBaseUrl: String,
    ) {
        prefs.edit()
            .putString(KEY_DEVICE_ID, deviceId)
            .putString(KEY_SITE_CODE, siteCode)
            .putString(KEY_LEGAL_ENTITY_ID, legalEntityId)
            .putString(KEY_CLOUD_BASE_URL, cloudBaseUrl)
            .putBoolean(KEY_IS_REGISTERED, true)
            .putBoolean(KEY_IS_DECOMMISSIONED, false)
            .apply()
        Log.i(TAG, "Registration data saved for site=$siteCode")
    }

    /**
     * Clear all registration data. Used during re-provisioning.
     */
    fun clearAll() {
        prefs.edit().clear().apply()
        Log.i(TAG, "All encrypted prefs cleared")
    }
}
