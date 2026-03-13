package com.fccmiddleware.edge.security

import android.content.Context
import android.content.SharedPreferences
import com.fccmiddleware.edge.logging.AppLogger
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import java.util.UUID

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
class EncryptedPrefsManager(context: Context, masterKey: MasterKey) {

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
        const val KEY_RUNTIME_CERTIFICATE_PINS = "runtime_certificate_pins"
        const val KEY_REPROVISIONING_REQUIRED = "reprovisioning_required"
        const val KEY_ENVIRONMENT = "environment"
        const val KEY_PROVISIONING_DEVICE_SERIAL_FALLBACK = "provisioning_device_serial_fallback"
        const val KEY_LAST_KEY_ROTATION_AT = "last_key_rotation_at"
    }

    // No fallback to regular SharedPreferences — storing sensitive identity data
    // (device tokens, site binding) unencrypted is a security violation.
    // If EncryptedSharedPreferences fails, the agent cannot operate safely.
    // AP-031: MasterKey is injected to avoid redundant Keystore IPC on startup.
    private val prefs: SharedPreferences = run {
        EncryptedSharedPreferences.create(
            context,
            PREFS_FILE,
            masterKey,
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
        set(value) {
            // M-01: Use commit() (synchronous) instead of apply() (async) to ensure the
            // decommission flag is durably persisted before any caller proceeds. A crash
            // between an async apply() and the disk flush could allow the device to restart
            // and resume sync despite being decommissioned.
            prefs.edit().putBoolean(KEY_IS_DECOMMISSIONED, value).commit()
        }

    /**
     * True if the refresh token has expired and the device needs re-provisioning.
     * Uses commit() (synchronous) for the same crash-safety reasons as [isDecommissioned].
     */
    /** Cloud environment key (e.g. "PRODUCTION", "STAGING"). Null for v1 QR / legacy registrations. */
    var environment: String?
        get() = prefs.getString(KEY_ENVIRONMENT, null)
        set(value) = prefs.edit().putString(KEY_ENVIRONMENT, value).apply()

    var isReprovisioningRequired: Boolean
        get() = prefs.getBoolean(KEY_REPROVISIONING_REQUIRED, false)
        set(value) {
            prefs.edit().putBoolean(KEY_REPROVISIONING_REQUIRED, value).commit()
        }

    /**
     * M-03: Atomically set isReprovisioningRequired=true and isRegistered=false
     * in a single synchronous commit(). Prevents partial state if the process
     * is killed between two separate writes.
     */
    fun setReprovisioningAndUnregister() {
        prefs.edit()
            .putBoolean(KEY_REPROVISIONING_REQUIRED, true)
            .putBoolean(KEY_IS_REGISTERED, false)
            .commit()
    }

    // ---- Batched update methods (AP-030) ----

    /**
     * AP-030: Batch FCC connection fields into a single editor/apply cycle.
     * On EncryptedSharedPreferences each apply() rewrites the entire encrypted XML;
     * batching avoids redundant AES encryption + disk write cycles on the hot path.
     */
    fun updateFccConnection(host: String? = null, port: Int? = null) {
        val editor = prefs.edit()
        if (host != null) editor.putString(KEY_FCC_HOST, host)
        if (port != null) editor.putInt(KEY_FCC_PORT, port)
        editor.apply()
    }

    // ---- Encrypted token blobs (Keystore-encrypted, stored as Base64) ----

    fun storeDeviceTokenBlob(encoded: String) {
        if (!prefs.edit().putString(KEY_DEVICE_TOKEN_ENCRYPTED, encoded).commit()) {
            AppLogger.e(TAG, "Failed to persist device token blob")
        }
    }

    fun getDeviceTokenBlob(): String? {
        return prefs.getString(KEY_DEVICE_TOKEN_ENCRYPTED, null)
    }

    fun storeRefreshTokenBlob(encoded: String) {
        if (!prefs.edit().putString(KEY_REFRESH_TOKEN_ENCRYPTED, encoded).commit()) {
            AppLogger.e(TAG, "Failed to persist refresh token blob")
        }
    }

    fun getRefreshTokenBlob(): String? {
        return prefs.getString(KEY_REFRESH_TOKEN_ENCRYPTED, null)
    }

    /**
     * Persist both token blobs in one synchronous commit so a process death cannot
     * leave the device with a new JWT blob and an old single-use refresh blob.
     */
    fun storeTokenBlobs(deviceBlob: String, refreshBlob: String): Boolean {
        return prefs.edit()
            .putString(KEY_DEVICE_TOKEN_ENCRYPTED, deviceBlob)
            .putString(KEY_REFRESH_TOKEN_ENCRYPTED, refreshBlob)
            .commit()
    }

    /**
     * Returns a stable fallback serial for devices where ANDROID_ID is unavailable.
     * Uses commit() so the generated value survives an app/process crash before the
     * next registration retry.
     */
    fun getOrCreateProvisioningDeviceSerialFallback(): String {
        val existing = prefs.getString(KEY_PROVISIONING_DEVICE_SERIAL_FALLBACK, null)
        if (!existing.isNullOrBlank()) return existing

        val generated = "unknown-${UUID.randomUUID().toString().take(8)}"
        prefs.edit().putString(KEY_PROVISIONING_DEVICE_SERIAL_FALLBACK, generated).commit()
        return generated
    }

    /**
     * Persist all registration data atomically after a successful registration.
     *
     * AF-053: Returns the result of commit() so callers can detect disk-full or
     * I/O failures. A false return means the data was NOT durably persisted.
     */
    fun saveRegistration(
        deviceId: String,
        siteCode: String,
        legalEntityId: String,
        cloudBaseUrl: String,
        environment: String? = null,
    ): Boolean {
        // Use commit() (synchronous) instead of apply() (async) to ensure registration
        // data is durably persisted before the foreground service starts. An async apply()
        // could race with the service reading isRegistered=false before the write completes.
        val committed = prefs.edit()
            .putString(KEY_DEVICE_ID, deviceId)
            .putString(KEY_SITE_CODE, siteCode)
            .putString(KEY_LEGAL_ENTITY_ID, legalEntityId)
            .putString(KEY_CLOUD_BASE_URL, cloudBaseUrl)
            .putString(KEY_ENVIRONMENT, environment)
            .putBoolean(KEY_IS_REGISTERED, true)
            .putBoolean(KEY_IS_DECOMMISSIONED, false)
            .putBoolean(KEY_REPROVISIONING_REQUIRED, false)
            .commit()
        if (committed) {
            AppLogger.i(TAG, "Registration data saved (env=${environment ?: "none"})")
        } else {
            AppLogger.e(TAG, "Failed to persist registration data — commit() returned false (disk full or I/O error)")
        }
        return committed
    }

    // ---- Key rotation tracking (AT-051) ----

    /** Epoch millis of the last successful Keystore key rotation. 0 = never rotated. */
    var lastKeyRotationAt: Long
        get() = prefs.getLong(KEY_LAST_KEY_ROTATION_AT, 0L)
        set(value) = prefs.edit().putLong(KEY_LAST_KEY_ROTATION_AT, value).apply()

    // ---- Runtime certificate pins (from SiteConfig) ----

    /**
     * Store runtime certificate pins delivered via SiteConfig from cloud.
     * These are preferred over bootstrap pins on next app restart.
     * Stored as comma-separated SHA-256 hashes.
     */
    var runtimeCertificatePins: List<String>
        get() {
            val stored = prefs.getString(KEY_RUNTIME_CERTIFICATE_PINS, null)
            return if (stored.isNullOrBlank()) emptyList()
            else stored.split(",").filter { it.isNotBlank() }
        }
        set(value) {
            if (value.isEmpty()) {
                prefs.edit().remove(KEY_RUNTIME_CERTIFICATE_PINS).apply()
            } else {
                prefs.edit().putString(KEY_RUNTIME_CERTIFICATE_PINS, value.joinToString(",")).apply()
            }
        }

    /**
     * Clear all registration data. Used during re-provisioning.
     */
    fun clearAll() {
        // Use commit() (synchronous) for consistency with other state-critical writes
        // (isDecommissioned, isReprovisioningRequired, saveRegistration). An async apply()
        // could leave stale flags on disk if the process is killed before the flush completes.
        prefs.edit().clear().commit()
        AppLogger.i(TAG, "All encrypted prefs cleared")
    }
}
