package com.fccmiddleware.edge.config

import android.content.Context
import android.content.SharedPreferences
import android.util.Base64
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.KeystoreManager

/**
 * Manages local FCC connection configuration overrides stored in
 * EncryptedSharedPreferences.
 *
 * Overrides are applied on top of cloud-delivered FCC config, allowing
 * on-site technicians to adjust connection parameters (host, port, credentials)
 * without waiting for a cloud config push.
 *
 * Override keys:
 *   - override_fcc_host       -- FCC controller LAN address (IPv4 or hostname)
 *   - override_fcc_port       -- FCC controller port
 *   - override_fcc_jpl_port   -- DOMS JPL binary-framed port
 *   - override_fcc_credential -- FCC auth credential
 *   - override_ws_port        -- WebSocket port
 *
 * NEVER log credential values.
 */
class LocalOverrideManager(
    context: Context,
    private val keystoreManager: KeystoreManager,
    masterKey: MasterKey,
) {

    companion object {
        private const val TAG = "LocalOverrideManager"
        private const val PREFS_FILE = "fcc_local_overrides"
        private const val KEY_FCC_CREDENTIAL_ENCRYPTED = "override_fcc_credential_enc"

        const val KEY_FCC_HOST = "override_fcc_host"
        const val KEY_FCC_PORT = "override_fcc_port"
        const val KEY_FCC_JPL_PORT = "override_fcc_jpl_port"
        const val KEY_FCC_CREDENTIAL = "override_fcc_credential"
        const val KEY_WS_PORT = "override_ws_port"

        private val PORT_KEYS = setOf(KEY_FCC_PORT, KEY_FCC_JPL_PORT, KEY_WS_PORT)
        private val HOST_KEYS = setOf(KEY_FCC_HOST)
        private val ALL_KEYS = setOf(KEY_FCC_HOST, KEY_FCC_PORT, KEY_FCC_JPL_PORT, KEY_FCC_CREDENTIAL, KEY_WS_PORT)

        private val IPV4_REGEX = Regex(
            "^((25[0-5]|2[0-4]\\d|[01]?\\d\\d?)\\.){3}(25[0-5]|2[0-4]\\d|[01]?\\d\\d?)$"
        )
        private val HOSTNAME_REGEX = Regex(
            "^([a-zA-Z0-9]([a-zA-Z0-9\\-]{0,61}[a-zA-Z0-9])?\\.)*[a-zA-Z0-9]([a-zA-Z0-9\\-]{0,61}[a-zA-Z0-9])?$"
        )

        fun isValidHostOrIp(value: String): Boolean {
            if (value.isBlank() || value.length > 253) return false
            return IPV4_REGEX.matches(value) || HOSTNAME_REGEX.matches(value)
        }

        fun isValidPort(port: Int): Boolean = port in 1..65535
    }

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

    // ---- Individual override getters ----

    val fccHost: String?
        get() = prefs.getString(KEY_FCC_HOST, null)

    val fccPort: Int?
        get() {
            val v = prefs.getInt(KEY_FCC_PORT, -1)
            return if (v > 0) v else null
        }

    val jplPort: Int?
        get() {
            val v = prefs.getInt(KEY_FCC_JPL_PORT, -1)
            return if (v > 0) v else null
        }

    val fccCredential: String?
        get() {
            val encrypted = prefs.getString(KEY_FCC_CREDENTIAL_ENCRYPTED, null)
            if (!encrypted.isNullOrBlank()) {
                val encryptedBytes = try {
                    Base64.decode(encrypted, Base64.NO_WRAP)
                } catch (e: Exception) {
                    AppLogger.e(TAG, "Failed to decode stored FCC credential override", e)
                    return null
                }
                return keystoreManager.retrieveSecret(KeystoreManager.ALIAS_FCC_CRED, encryptedBytes)
            }

            val legacyPlaintext = prefs.getString(KEY_FCC_CREDENTIAL, null)
            if (legacyPlaintext.isNullOrBlank()) return null

            return try {
                persistEncryptedCredential(legacyPlaintext)
                prefs.edit().remove(KEY_FCC_CREDENTIAL).apply()
                AppLogger.i(TAG, "Migrated legacy FCC credential override to Keystore-backed storage")
                legacyPlaintext
            } catch (e: Exception) {
                AppLogger.e(TAG, "Failed to migrate legacy FCC credential override", e)
                legacyPlaintext
            }
        }

    val wsPort: Int?
        get() {
            val v = prefs.getInt(KEY_WS_PORT, -1)
            return if (v > 0) v else null
        }

    // ---- Save / clear ----

    /**
     * Saves an override value after validation.
     *
     * @throws IllegalArgumentException if the key is unrecognized, the host
     *   is not a valid IPv4 address or hostname, or the port is outside 1-65535.
     */
    fun saveOverride(key: String, value: String) {
        require(key in ALL_KEYS) { "Unknown override key: '$key'" }

        when {
            key in HOST_KEYS -> {
                require(isValidHostOrIp(value)) {
                    "Invalid host/IP: '$value'. Must be a valid IPv4 address or hostname."
                }
                prefs.edit().putString(key, value).apply()
            }
            key in PORT_KEYS -> {
                val port = value.toIntOrNull()
                    ?: throw IllegalArgumentException("Port must be a number: '$value'")
                require(isValidPort(port)) { "Port out of range: $port. Must be 1-65535." }
                prefs.edit().putInt(key, port).apply()
            }
            key == KEY_FCC_CREDENTIAL -> {
                persistEncryptedCredential(value)
            }
        }
        AppLogger.i(TAG, "Override saved: $key")
    }

    /**
     * Clears a single override, reverting to the cloud-delivered value.
     */
    fun clearOverride(key: String) {
        if (key == KEY_FCC_CREDENTIAL) {
            prefs.edit()
                .remove(KEY_FCC_CREDENTIAL)
                .remove(KEY_FCC_CREDENTIAL_ENCRYPTED)
                .apply()
            keystoreManager.deleteKey(KeystoreManager.ALIAS_FCC_CRED)
        } else {
            prefs.edit().remove(key).apply()
        }
        AppLogger.i(TAG, "Override cleared: $key")
    }

    /**
     * Clears all overrides, restoring to cloud defaults.
     */
    fun clearAllOverrides() {
        prefs.edit().clear().apply()
        keystoreManager.deleteKey(KeystoreManager.ALIAS_FCC_CRED)
        AppLogger.i(TAG, "All overrides cleared")
    }

    /**
     * Returns true if any override values are currently set.
     */
    fun hasAnyOverrides(): Boolean = prefs.all.isNotEmpty()

    /**
     * Merges local overrides onto a cloud-delivered [FccDto], returning a copy
     * with overridden fields replaced. Fields without overrides retain their
     * cloud values.
     *
     * Note: jplPort and wsPort overrides are applied separately in
     * [toAgentFccConfig] since they map to [AgentFccConfig] fields not present
     * in [FccDto].
     */
    fun getOverriddenFccConfig(cloudConfig: FccDto): FccDto {
        if (!hasAnyOverrides()) return cloudConfig
        return cloudConfig.copy(
            hostAddress = fccHost ?: cloudConfig.hostAddress,
            port = fccPort ?: cloudConfig.port,
            credentialRef = fccCredential ?: cloudConfig.credentialRef,
        )
    }

    private fun persistEncryptedCredential(value: String) {
        val encrypted = keystoreManager.storeSecret(KeystoreManager.ALIAS_FCC_CRED, value)
            ?: throw IllegalStateException("Failed to encrypt FCC credential override")
        prefs.edit()
            .remove(KEY_FCC_CREDENTIAL)
            .putString(
                KEY_FCC_CREDENTIAL_ENCRYPTED,
                Base64.encodeToString(encrypted, Base64.NO_WRAP),
            )
            .apply()
    }
}
