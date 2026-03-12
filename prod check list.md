# Production Deployment Checklist

## Field Encryption Key (Required)

Set `FieldEncryption:Key` in application configuration before deploying the at-rest encryption changes.

- **Format:** 64-character hex string (32 bytes for AES-256)
- **Example generation:** `openssl rand -hex 32`
- **Configuration path:** `FieldEncryption:Key` (environment variable: `FieldEncryption__Key`)
- **Scope:** Must be consistent across all API instances sharing the same database

Existing plaintext secret values (`shared_secret`, `fc_access_code`, `client_secret`, `webhook_secret`, `advatec_webhook_token`) will remain readable without this key. Once the key is set, values are encrypted on next write and decrypted transparently on read (incremental migration — no bulk update required).

**Key rotation:** To rotate, decrypt all values with the old key, then re-encrypt with the new key. A bulk migration script should be prepared before rotation.
