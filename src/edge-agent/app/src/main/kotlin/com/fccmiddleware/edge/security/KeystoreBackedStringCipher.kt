package com.fccmiddleware.edge.security

import java.util.Base64

/**
 * Stores sensitive strings as Keystore-encrypted Base64 blobs with a stable prefix.
 */
class KeystoreBackedStringCipher(
    private val keystoreManager: KeystoreManager?,
    private val alias: String,
    private val prefix: String = ENCRYPTED_PREFIX_V1,
) {

    companion object {
        const val ENCRYPTED_PREFIX_V1 = "ENCv1:"
    }

    fun isEncrypted(value: String?): Boolean = value?.startsWith(prefix) == true

    fun encryptForStorage(plaintext: String?): String? {
        if (plaintext == null) return null
        if (plaintext.isBlank()) return plaintext

        val manager = keystoreManager
            ?: throw IllegalStateException("KeystoreManager is not configured")
        val encryptedBytes = manager.storeSecret(alias, plaintext)
            ?: throw IllegalStateException("Keystore encryption failed")
        return prefix + Base64.getEncoder().encodeToString(encryptedBytes)
    }

    fun decryptFromStorage(storedValue: String?): String? {
        if (storedValue == null) return null
        if (!isEncrypted(storedValue)) return storedValue

        val manager = keystoreManager
            ?: throw IllegalStateException("KeystoreManager is not configured")
        val encoded = storedValue.removePrefix(prefix)
        val encryptedBytes = try {
            Base64.getDecoder().decode(encoded)
        } catch (e: IllegalArgumentException) {
            throw IllegalStateException("Stored ciphertext is not valid Base64", e)
        }

        return manager.retrieveSecret(alias, encryptedBytes)
            ?: throw IllegalStateException("Keystore decryption failed")
    }
}
