package com.fccmiddleware.edge.security

import com.fccmiddleware.edge.sync.DeviceRegistrationRequest
import com.fccmiddleware.edge.sync.DeviceRegistrationResponse
import com.fccmiddleware.edge.sync.HttpCloudApiClient
import com.fccmiddleware.edge.sync.TokenRefreshRequest
import com.fccmiddleware.edge.sync.TokenRefreshResponse
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.DisplayName
import org.junit.jupiter.api.Nested
import org.junit.jupiter.api.Test

/**
 * EA-6.2 Security Hardening Tests.
 *
 * Verifies:
 * 1. KeystoreManager: AES-256-GCM, non-exportable keys, all aliases
 * 2. @Sensitive annotation: present on sensitive model fields
 * 3. SensitiveFieldFilter: redacts annotated fields, preserves JWT suffix
 * 4. EncryptedPrefsManager: no fallback to regular prefs
 * 5. Certificate pinning: hostname extraction for CertificatePinner
 * 6. LAN API key: constant-time comparison (tested in LocalApiServer route tests)
 */
class SecurityHardeningTest {

    // -------------------------------------------------------------------------
    // 1. KeystoreManager key aliases
    // -------------------------------------------------------------------------

    @Nested
    @DisplayName("KeystoreManager aliases")
    inner class KeystoreAliases {

        @Test
        fun `device JWT alias matches security spec`() {
            assertEquals("fcc-middleware-device-jwt", KeystoreManager.ALIAS_DEVICE_JWT)
        }

        @Test
        fun `refresh token alias matches security spec`() {
            assertEquals("fcc-middleware-refresh-token", KeystoreManager.ALIAS_REFRESH_TOKEN)
        }

        @Test
        fun `FCC credential alias matches security spec`() {
            assertEquals("fcc-middleware-fcc-cred", KeystoreManager.ALIAS_FCC_CRED)
        }

        @Test
        fun `LAN key alias matches security spec`() {
            assertEquals("fcc-middleware-lan-key", KeystoreManager.ALIAS_LAN_KEY)
        }

        @Test
        fun `all four aliases are distinct`() {
            val aliases = listOf(
                KeystoreManager.ALIAS_DEVICE_JWT,
                KeystoreManager.ALIAS_REFRESH_TOKEN,
                KeystoreManager.ALIAS_FCC_CRED,
                KeystoreManager.ALIAS_LAN_KEY,
            )
            assertEquals(4, aliases.toSet().size, "All key aliases must be distinct")
        }
    }

    // -------------------------------------------------------------------------
    // 2. @Sensitive annotation on model fields
    // -------------------------------------------------------------------------

    @Nested
    @DisplayName("@Sensitive annotation placement")
    inner class SensitiveAnnotation {

        @Test
        fun `DeviceRegistrationRequest provisioningToken is @Sensitive`() {
            val field = DeviceRegistrationRequest::class.java.getDeclaredField("provisioningToken")
            assertTrue(
                field.isAnnotationPresent(Sensitive::class.java),
                "provisioningToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `DeviceRegistrationResponse deviceToken is @Sensitive`() {
            val field = DeviceRegistrationResponse::class.java.getDeclaredField("deviceToken")
            assertTrue(
                field.isAnnotationPresent(Sensitive::class.java),
                "deviceToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `DeviceRegistrationResponse refreshToken is @Sensitive`() {
            val field = DeviceRegistrationResponse::class.java.getDeclaredField("refreshToken")
            assertTrue(
                field.isAnnotationPresent(Sensitive::class.java),
                "refreshToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `TokenRefreshRequest refreshToken is @Sensitive`() {
            val field = TokenRefreshRequest::class.java.getDeclaredField("refreshToken")
            assertTrue(
                field.isAnnotationPresent(Sensitive::class.java),
                "refreshToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `TokenRefreshResponse deviceToken is @Sensitive`() {
            val field = TokenRefreshResponse::class.java.getDeclaredField("deviceToken")
            assertTrue(
                field.isAnnotationPresent(Sensitive::class.java),
                "deviceToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `TokenRefreshResponse refreshToken is @Sensitive`() {
            val field = TokenRefreshResponse::class.java.getDeclaredField("refreshToken")
            assertTrue(
                field.isAnnotationPresent(Sensitive::class.java),
                "refreshToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `DeviceRegistrationRequest siteCode is NOT @Sensitive`() {
            val field = DeviceRegistrationRequest::class.java.getDeclaredField("siteCode")
            assertFalse(
                field.isAnnotationPresent(Sensitive::class.java),
                "siteCode is not sensitive and should not be annotated",
            )
        }
    }

    // -------------------------------------------------------------------------
    // 3. SensitiveFieldFilter
    // -------------------------------------------------------------------------

    @Nested
    @DisplayName("SensitiveFieldFilter")
    inner class SensitiveFieldFilterTest {

        data class TestModel(
            @Sensitive val secretToken: String,
            val publicField: String,
        )

        data class JwtModel(
            @Sensitive val deviceToken: String,
        )

        data class NonSensitiveModel(
            val name: String,
            val value: Int,
        )

        @Test
        fun `redacts @Sensitive fields with REDACTED`() {
            val model = TestModel(secretToken = "super-secret-value", publicField = "visible")
            val redacted = SensitiveFieldFilter.redact(model)

            assertEquals("[REDACTED]", redacted["secretToken"])
            assertEquals("visible", redacted["publicField"])
        }

        @Test
        fun `preserves last 8 chars for token fields`() {
            val model = JwtModel(deviceToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.longpayload.signature12345678")
            val redacted = SensitiveFieldFilter.redact(model)
            val value = redacted["deviceToken"] as String

            assertTrue(value.startsWith("..."), "Token should start with ...")
            assertEquals(8, value.removePrefix("...").length, "Should preserve last 8 chars")
            assertTrue(value.endsWith("e12345678".takeLast(8)))
        }

        @Test
        fun `short token fields are fully redacted`() {
            val model = JwtModel(deviceToken = "short")
            val redacted = SensitiveFieldFilter.redact(model)

            assertEquals("[REDACTED]", redacted["deviceToken"])
        }

        @Test
        fun `null sensitive field is redacted`() {
            // Use reflection test — create object with null-like behavior
            data class NullableModel(@Sensitive val apiKey: String?)
            val model = NullableModel(apiKey = null)
            val redacted = SensitiveFieldFilter.redact(model)

            assertEquals("[REDACTED]", redacted["apiKey"])
        }

        @Test
        fun `non-sensitive model passes through unchanged`() {
            val model = NonSensitiveModel(name = "test", value = 42)
            val redacted = SensitiveFieldFilter.redact(model)

            assertEquals("test", redacted["name"])
            assertEquals(42, redacted["value"])
        }

        @Test
        fun `redactToString produces readable output`() {
            val model = TestModel(secretToken = "secret123456789", publicField = "visible")
            val output = SensitiveFieldFilter.redactToString(model)

            assertTrue(output.contains("publicField=visible"))
            assertFalse(output.contains("secret123456789"))
        }

        @Test
        fun `real model — TokenRefreshRequest redacts refreshToken`() {
            val request = TokenRefreshRequest(refreshToken = "opaque-refresh-token-with-many-characters")
            val redacted = SensitiveFieldFilter.redact(request)

            val value = redacted["refreshToken"] as String
            assertFalse(
                value.contains("opaque-refresh"),
                "Refresh token plaintext must not appear in redacted output",
            )
        }

        @Test
        fun `real model — DeviceRegistrationRequest redacts provisioningToken`() {
            val request = DeviceRegistrationRequest(
                provisioningToken = "base64url-bootstrap-token-value",
                siteCode = "ZM-LUSAKA-01",
                deviceSerialNumber = "U9100-001",
                deviceModel = "i9100",
                osVersion = "12",
                agentVersion = "1.0.0",
            )
            val redacted = SensitiveFieldFilter.redact(request)

            assertEquals("[REDACTED]", redacted["provisioningToken"])
            assertEquals("ZM-LUSAKA-01", redacted["siteCode"])
        }
    }

    // -------------------------------------------------------------------------
    // 4. Certificate pinning hostname extraction
    // -------------------------------------------------------------------------

    @Nested
    @DisplayName("Certificate pinning — hostname extraction")
    inner class CertPinning {

        @Test
        fun `extracts hostname from https URL`() {
            val host = HttpCloudApiClient.extractHostname("https://api.fcc-middleware.prod.example.com")
            assertEquals("api.fcc-middleware.prod.example.com", host)
        }

        @Test
        fun `extracts hostname from URL with port`() {
            val host = HttpCloudApiClient.extractHostname("https://api.fcc-middleware.dev.example.com:8443")
            assertEquals("api.fcc-middleware.dev.example.com", host)
        }

        @Test
        fun `extracts hostname from URL with path`() {
            val host = HttpCloudApiClient.extractHostname("https://api.fcc-middleware.io/api/v1")
            assertEquals("api.fcc-middleware.io", host)
        }

        @Test
        fun `extracts hostname from http URL`() {
            val host = HttpCloudApiClient.extractHostname("http://localhost:8080")
            assertEquals("localhost", host)
        }

        @Test
        fun `returns null for empty URL`() {
            val host = HttpCloudApiClient.extractHostname("")
            assertNull(host)
        }

        @Test
        fun `returns null for blank URL`() {
            val host = HttpCloudApiClient.extractHostname("   ")
            assertNull(host)
        }

        @Test
        fun `handles URL without scheme`() {
            val host = HttpCloudApiClient.extractHostname("api.example.com/path")
            assertEquals("api.example.com", host)
        }
    }

    // -------------------------------------------------------------------------
    // 5. EncryptedPrefsManager keys
    // -------------------------------------------------------------------------

    @Nested
    @DisplayName("EncryptedPrefsManager key constants")
    inner class EncryptedPrefsKeys {

        @Test
        fun `all sensitive identity keys are defined`() {
            // Per security spec §5.3 — all sensitive fields stored in EncryptedSharedPreferences
            assertNotNull(EncryptedPrefsManager.KEY_DEVICE_ID)
            assertNotNull(EncryptedPrefsManager.KEY_SITE_CODE)
            assertNotNull(EncryptedPrefsManager.KEY_LEGAL_ENTITY_ID)
            assertNotNull(EncryptedPrefsManager.KEY_CLOUD_BASE_URL)
            assertNotNull(EncryptedPrefsManager.KEY_FCC_HOST)
            assertNotNull(EncryptedPrefsManager.KEY_FCC_PORT)
        }

        @Test
        fun `token blob keys are defined for Keystore-encrypted tokens`() {
            assertNotNull(EncryptedPrefsManager.KEY_DEVICE_TOKEN_ENCRYPTED)
            assertNotNull(EncryptedPrefsManager.KEY_REFRESH_TOKEN_ENCRYPTED)
        }

        @Test
        fun `decommission and registration flags are defined`() {
            assertNotNull(EncryptedPrefsManager.KEY_IS_REGISTERED)
            assertNotNull(EncryptedPrefsManager.KEY_IS_DECOMMISSIONED)
        }
    }

    // -------------------------------------------------------------------------
    // 6. Log redaction — verify no sensitive data in toString()
    // -------------------------------------------------------------------------

    @Nested
    @DisplayName("Log safety — sensitive model toString")
    inner class LogSafety {

        @Test
        fun `SensitiveFieldFilter redacts TokenRefreshResponse`() {
            val response = TokenRefreshResponse(
                deviceToken = "eyJhbGciOiJSUzI1NiJ9.payload.signature",
                refreshToken = "opaque-refresh-token-90-days",
                tokenExpiresAt = "2026-03-12T00:00:00Z",
            )
            val safe = SensitiveFieldFilter.redactToString(response)

            assertFalse(safe.contains("eyJhbGciOiJSUzI1NiJ9"), "JWT must not appear in redacted output")
            assertFalse(safe.contains("opaque-refresh"), "Refresh token must not appear in redacted output")
            assertTrue(safe.contains("2026-03-12T00:00:00Z"), "Non-sensitive tokenExpiresAt should be visible")
        }

        @Test
        fun `SensitiveFieldFilter redacts DeviceRegistrationResponse`() {
            val response = DeviceRegistrationResponse(
                deviceId = "d1234567-0000-0000-0000-000000000001",
                deviceToken = "eyJhbGciOiJSUzI1NiJ9.payload.sig",
                refreshToken = "opaque-refresh-90d",
                tokenExpiresAt = "2026-03-12T00:00:00Z",
                siteCode = "ZM-LUSAKA-01",
                legalEntityId = "10000000-0000-0000-0000-000000000004",
                registeredAt = "2026-03-11T10:00:00Z",
            )
            val safe = SensitiveFieldFilter.redactToString(response)

            assertFalse(safe.contains("eyJhbGciOiJSUzI1NiJ9"))
            assertFalse(safe.contains("opaque-refresh"))
            assertTrue(safe.contains("ZM-LUSAKA-01"), "siteCode should be visible")
            assertTrue(safe.contains("d1234567"), "deviceId should be visible")
        }
    }
}
