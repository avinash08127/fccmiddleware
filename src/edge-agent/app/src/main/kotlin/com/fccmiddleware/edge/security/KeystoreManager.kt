package com.fccmiddleware.edge.security

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import com.fccmiddleware.edge.logging.AppLogger
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * KeystoreManager — wraps Android Keystore for secure token storage.
 *
 * Stores opaque secrets (device JWT, refresh token, FCC credentials, LAN API key)
 * encrypted with AES-256-GCM keys backed by hardware TEE where available (Urovo i9100).
 *
 * Key aliases per the security spec:
 *   - fcc-middleware-device-jwt     — current device access token
 *   - fcc-middleware-refresh-token  — current refresh token (90-day opaque)
 *   - fcc-middleware-fcc-cred       — FCC credential encryption
 *   - fcc-middleware-lan-key        — LAN API key for multi-HHT
 *
 * All keys are non-exportable and do not require user authentication.
 * NEVER log token values or credential content.
 */
class KeystoreManager {

    companion object {
        private const val TAG = "KeystoreManager"
        private const val KEYSTORE_PROVIDER = "AndroidKeyStore"
        private const val GCM_TAG_LENGTH = 128

        const val ALIAS_DEVICE_JWT = "fcc-middleware-device-jwt"
        const val ALIAS_REFRESH_TOKEN = "fcc-middleware-refresh-token"
        const val ALIAS_FCC_CRED = "fcc-middleware-fcc-cred"
        const val ALIAS_LAN_KEY = "fcc-middleware-lan-key"
        const val ALIAS_CONFIG_INTEGRITY = "fcc-middleware-config-integrity"
    }

    private val keyStore: KeyStore = KeyStore.getInstance(KEYSTORE_PROVIDER).apply { load(null) }

    /**
     * Store a secret string under the given alias.
     * Creates a new AES-256-GCM key if one doesn't exist for the alias.
     * The IV is prepended to the ciphertext for self-contained decryption.
     *
     * @return Encrypted bytes (IV + ciphertext) or null on failure.
     */
    fun storeSecret(alias: String, plaintext: String): ByteArray? {
        return try {
            val key = getOrCreateKey(alias)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, key)
            val iv = cipher.iv
            val ciphertext = cipher.doFinal(plaintext.toByteArray(Charsets.UTF_8))
            // Prepend IV length (1 byte) + IV + ciphertext
            byteArrayOf(iv.size.toByte()) + iv + ciphertext
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to store secret for alias=$alias", e)
            null
        }
    }

    /**
     * Retrieve a secret string stored under the given alias.
     *
     * @param alias Keystore alias used during [storeSecret].
     * @param encrypted The encrypted bytes returned by [storeSecret].
     * @return Decrypted plaintext or null on failure.
     */
    fun retrieveSecret(alias: String, encrypted: ByteArray): String? {
        return try {
            if (encrypted.size < 2) {
                AppLogger.w(TAG, "Encrypted blob too short for alias=$alias (size=${encrypted.size})")
                return null
            }
            val key = keyStore.getKey(alias, null) as? SecretKey ?: run {
                AppLogger.w(TAG, "No key found for alias=$alias")
                return null
            }
            val ivLength = encrypted[0].toInt() and 0xFF
            if (ivLength == 0 || 1 + ivLength >= encrypted.size) {
                AppLogger.w(TAG, "Invalid IV length=$ivLength for alias=$alias (blob size=${encrypted.size})")
                return null
            }
            val iv = encrypted.sliceArray(1..ivLength)
            val ciphertext = encrypted.sliceArray((1 + ivLength) until encrypted.size)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(GCM_TAG_LENGTH, iv))
            String(cipher.doFinal(ciphertext), Charsets.UTF_8)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to retrieve secret for alias=$alias", e)
            null
        }
    }

    /**
     * Delete a key and its associated encrypted data from the Keystore.
     */
    fun deleteKey(alias: String) {
        try {
            if (keyStore.containsAlias(alias)) {
                keyStore.deleteEntry(alias)
                AppLogger.d(TAG, "Deleted key alias=$alias")
            }
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to delete key alias=$alias", e)
        }
    }

    /** True if a key exists for the given alias. */
    fun hasKey(alias: String): Boolean {
        return try {
            keyStore.containsAlias(alias)
        } catch (e: Exception) {
            false
        }
    }

    /**
     * Rotate the AES-256-GCM key for the given alias.
     *
     * Decrypts [currentEncrypted] with the existing key, deletes the old key,
     * generates a fresh key, and re-encrypts the plaintext under the new key.
     *
     * @param alias Keystore alias to rotate.
     * @param currentEncrypted The encrypted blob produced by [storeSecret] under the current key.
     * @return New encrypted bytes (IV + ciphertext) under the rotated key, or null on failure.
     */
    fun rotateKey(alias: String, currentEncrypted: ByteArray): ByteArray? {
        return try {
            val plaintext = retrieveSecret(alias, currentEncrypted) ?: run {
                AppLogger.e(TAG, "Key rotation failed for alias=$alias — could not decrypt current data")
                return null
            }
            deleteKey(alias)
            storeSecret(alias, plaintext)
        } catch (e: Exception) {
            AppLogger.e(TAG, "Key rotation failed for alias=$alias", e)
            null
        }
    }

    /**
     * Delete all FCC middleware keys from the Keystore.
     * Used during re-provisioning or factory reset.
     */
    fun clearAll() {
        listOf(
            ALIAS_DEVICE_JWT, ALIAS_REFRESH_TOKEN, ALIAS_FCC_CRED,
            ALIAS_LAN_KEY, ALIAS_CONFIG_INTEGRITY,
        ).forEach {
            deleteKey(it)
        }
        AppLogger.i(TAG, "All keystore keys cleared")
    }

    private fun getOrCreateKey(alias: String): SecretKey {
        val existing = keyStore.getKey(alias, null) as? SecretKey
        if (existing != null) return existing

        val spec = KeyGenParameterSpec.Builder(
            alias,
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
        )
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .setUserAuthenticationRequired(false)
            .build()

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE_PROVIDER)
        generator.init(spec)
        return generator.generateKey()
    }
}
