package com.fccmiddleware.edge.security

/**
 * KeystoreManager — wraps Android Keystore for secure key storage.
 *
 * Used for:
 *   - Agent registration token (bootstrap secret)
 *   - LAN API key (for multi-device scenarios)
 *   - FCC credential encryption key
 *
 * NEVER log sensitive fields (tokens, credentials, TINs).
 *
 * Stub — implementation follows EA-2.x security tasks.
 */
class KeystoreManager {
    // TODO (EA-2.x): implement Android Keystore key generation and retrieval
}
