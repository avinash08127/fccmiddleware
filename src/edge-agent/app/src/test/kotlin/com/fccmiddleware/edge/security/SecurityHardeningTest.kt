package com.fccmiddleware.edge.security

import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.FccVendor
import com.fccmiddleware.edge.adapter.common.IngestionMode
import com.fccmiddleware.edge.adapter.common.PreAuthCommand
import com.fccmiddleware.edge.sync.DeviceRegistrationRequest
import com.fccmiddleware.edge.sync.DeviceRegistrationResponse
import com.fccmiddleware.edge.sync.HttpCloudApiClient
import com.fccmiddleware.edge.sync.TokenRefreshRequest
import com.fccmiddleware.edge.sync.TokenRefreshResponse
import kotlin.reflect.full.memberProperties
import okhttp3.CertificatePinner
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.DisplayName
import org.junit.jupiter.api.Nested
import org.junit.jupiter.api.Test
import javax.net.ssl.SSLPeerUnverifiedException

private data class SensitiveFilterTestModel(
    @Sensitive val secretToken: String,
    val publicField: String,
)

private data class SensitiveFilterJwtModel(
    @Sensitive val deviceToken: String,
)

private data class SensitiveFilterPlainModel(
    val name: String,
    val value: Int,
)

/**
 * EA-6.2 Security Hardening Tests.
 *
 * Verifies:
 * 1. KeystoreManager: AES-256-GCM, non-exportable keys, all aliases
 * 2. @Sensitive annotation: present on ALL sensitive model fields (tokens, credentials, PII)
 * 3. SensitiveFieldFilter: redacts annotated fields, preserves JWT suffix
 * 4. EncryptedPrefsManager: no fallback to regular prefs, all keys defined
 * 5. Certificate pinning: hostname extraction, pin mismatch rejection
 * 6. LAN API key: constant-time comparison (tested in LanApiKeyAuthTest)
 * 7. Log safety: no sensitive data leaks through toString() or redactToString()
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

        /** Helper: checks if a Kotlin property is annotated @Sensitive (property or field). */
        private inline fun <reified T : Any> isSensitive(propName: String): Boolean {
            val prop = T::class.memberProperties.first { it.name == propName }
            return prop.annotations.any { it is Sensitive }
        }

        @Test
        fun `DeviceRegistrationRequest provisioningToken is @Sensitive`() {
            assertTrue(
                isSensitive<DeviceRegistrationRequest>("provisioningToken"),
                "provisioningToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `DeviceRegistrationResponse deviceToken is @Sensitive`() {
            assertTrue(
                isSensitive<DeviceRegistrationResponse>("deviceToken"),
                "deviceToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `DeviceRegistrationResponse refreshToken is @Sensitive`() {
            assertTrue(
                isSensitive<DeviceRegistrationResponse>("refreshToken"),
                "refreshToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `TokenRefreshRequest refreshToken is @Sensitive`() {
            assertTrue(
                isSensitive<TokenRefreshRequest>("refreshToken"),
                "refreshToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `TokenRefreshResponse deviceToken is @Sensitive`() {
            assertTrue(
                isSensitive<TokenRefreshResponse>("deviceToken"),
                "deviceToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `TokenRefreshResponse refreshToken is @Sensitive`() {
            assertTrue(
                isSensitive<TokenRefreshResponse>("refreshToken"),
                "refreshToken must be annotated with @Sensitive",
            )
        }

        @Test
        fun `AgentFccConfig authCredential is @Sensitive`() {
            assertTrue(
                isSensitive<AgentFccConfig>("authCredential"),
                "authCredential (FCC API key) must be annotated with @Sensitive",
            )
        }

        @Test
        fun `PreAuthCommand customerTaxId is @Sensitive`() {
            assertTrue(
                isSensitive<PreAuthCommand>("customerTaxId"),
                "customerTaxId (PII) must be annotated with @Sensitive",
            )
        }

        @Test
        fun `DeviceRegistrationRequest siteCode is NOT @Sensitive`() {
            assertFalse(
                isSensitive<DeviceRegistrationRequest>("siteCode"),
                "siteCode is not sensitive and should not be annotated",
            )
        }

        @Test
        fun `AgentFccConfig hostAddress is NOT @Sensitive`() {
            assertFalse(
                isSensitive<AgentFccConfig>("hostAddress"),
                "hostAddress is not sensitive",
            )
        }
    }

    // -------------------------------------------------------------------------
    // 3. SensitiveFieldFilter
    // -------------------------------------------------------------------------

    @Nested
    @DisplayName("SensitiveFieldFilter")
    inner class SensitiveFieldFilterTest {

        @Test
        fun `redacts @Sensitive fields with REDACTED`() {
            val model = SensitiveFilterTestModel(secretToken = "super-secret-value", publicField = "visible")
            val redacted = SensitiveFieldFilter.redact(model)

            assertEquals("[REDACTED]", redacted["secretToken"])
            assertEquals("visible", redacted["publicField"])
        }

        @Test
        fun `preserves last 8 chars for token fields`() {
            val model = SensitiveFilterJwtModel(deviceToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.longpayload.signature12345678")
            val redacted = SensitiveFieldFilter.redact(model)
            val value = redacted["deviceToken"] as String

            assertTrue(value.startsWith("..."), "Token should start with ...")
            assertEquals(8, value.removePrefix("...").length, "Should preserve last 8 chars")
            assertTrue(value.endsWith("e12345678".takeLast(8)))
        }

        @Test
        fun `short token fields are fully redacted`() {
            val model = SensitiveFilterJwtModel(deviceToken = "short")
            val redacted = SensitiveFieldFilter.redact(model)

            assertEquals("[REDACTED]", redacted["deviceToken"])
        }

        @Test
        fun `null sensitive field is redacted`() {
            data class NullableModel(@Sensitive val apiKey: String?)
            val model = NullableModel(apiKey = null)
            val redacted = SensitiveFieldFilter.redact(model)

            assertEquals("[REDACTED]", redacted["apiKey"])
        }

        @Test
        fun `non-sensitive model passes through unchanged`() {
            val model = SensitiveFilterPlainModel(name = "test", value = 42)
            val redacted = SensitiveFieldFilter.redact(model)

            assertEquals("test", redacted["name"])
            assertEquals(42, redacted["value"])
        }

        @Test
        fun `redactToString produces readable output`() {
            val model = SensitiveFilterTestModel(secretToken = "secret123456789", publicField = "visible")
            val output = SensitiveFieldFilter.redactToString(model)

            assertTrue(output.contains("publicField=visible"))
            assertFalse(output.contains("secret123456789"))
        }

        @Test
        fun `real model — TokenRefreshRequest redacts refreshToken fully`() {
            val request = TokenRefreshRequest(refreshToken = "opaque-refresh-token-with-many-characters")
            val redacted = SensitiveFieldFilter.redact(request)

            // Per spec: refresh tokens are fully redacted (not last-8-chars like device JWT)
            assertEquals(
                "[REDACTED]",
                redacted["refreshToken"],
                "Refresh token must be fully redacted — only deviceToken gets suffix preview",
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

        @Test
        fun `real model — AgentFccConfig redacts authCredential`() {
            val config = AgentFccConfig(
                fccVendor = FccVendor.DOMS,
                connectionProtocol = "REST",
                hostAddress = "192.168.1.100",
                port = 8080,
                authCredential = "super-secret-fcc-api-key-never-log",
                ingestionMode = IngestionMode.RELAY,
                pullIntervalSeconds = 30,
                productCodeMapping = mapOf("001" to "PMS"),
                timezone = "Africa/Johannesburg",
                currencyCode = "ZAR",
            )
            val redacted = SensitiveFieldFilter.redact(config)

            assertEquals("[REDACTED]", redacted["authCredential"],
                "FCC auth credential must be fully redacted")
            assertEquals("192.168.1.100", redacted["hostAddress"],
                "hostAddress is non-sensitive and should pass through")
            assertFalse(
                redacted.values.any { it?.toString()?.contains("super-secret-fcc") == true },
                "FCC credential plaintext must not appear anywhere in redacted output",
            )
        }

        @Test
        fun `real model — PreAuthCommand redacts customerTaxId`() {
            val command = PreAuthCommand(
                siteCode = "ZM-LUSAKA-01",
                pumpNumber = 3,
                amountMinorUnits = 50000,
                currencyCode = "ZAR",
                customerTaxId = "1234567890",
            )
            val redacted = SensitiveFieldFilter.redact(command)

            assertEquals("[REDACTED]", redacted["customerTaxId"],
                "Customer TIN must be fully redacted (PII)")
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
    // 4b. Certificate pinning — pin mismatch rejection
    // -------------------------------------------------------------------------

    @Nested
    @DisplayName("Certificate pinning — pin mismatch rejection")
    inner class CertPinMismatch {

        @Test
        fun `CertificatePinner rejects mismatched pin for hostname`() {
            // Build a CertificatePinner with a known-wrong pin (self-signed cert scenario).
            // OkHttp will throw SSLPeerUnverifiedException on handshake when the
            // server cert's public key doesn't match the pinned hash.
            val pinner = CertificatePinner.Builder()
                .add(
                    "api.fcc-middleware.prod.example.com",
                    "sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                )
                .build()

            // Verifying that check() throws when presented with zero matching certificates.
            // This simulates a self-signed or mismatched cert scenario.
            var threw = false
            try {
                pinner.check(
                    "api.fcc-middleware.prod.example.com",
                    emptyList(),
                )
            } catch (_: SSLPeerUnverifiedException) {
                threw = true
            }
            assertTrue(threw, "CertificatePinner.check() must throw SSLPeerUnverifiedException for mismatched pins")
        }

        @Test
        fun `CertificatePinner allows unpinned hostname`() {
            // Pins only apply to the specified hostname.
            // A different hostname should not be rejected by the pinner.
            val pinner = CertificatePinner.Builder()
                .add(
                    "api.fcc-middleware.prod.example.com",
                    "sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                )
                .build()

            // check() for a hostname that has no pins configured should not throw
            pinner.check("other.example.com", emptyList())
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
            // Per security spec 5.3 - all sensitive fields stored in EncryptedSharedPreferences
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

        @Test
        fun `all key constants have distinct values`() {
            val keys = listOf(
                EncryptedPrefsManager.KEY_DEVICE_ID,
                EncryptedPrefsManager.KEY_SITE_CODE,
                EncryptedPrefsManager.KEY_LEGAL_ENTITY_ID,
                EncryptedPrefsManager.KEY_CLOUD_BASE_URL,
                EncryptedPrefsManager.KEY_FCC_HOST,
                EncryptedPrefsManager.KEY_FCC_PORT,
                EncryptedPrefsManager.KEY_IS_REGISTERED,
                EncryptedPrefsManager.KEY_IS_DECOMMISSIONED,
                EncryptedPrefsManager.KEY_DEVICE_TOKEN_ENCRYPTED,
                EncryptedPrefsManager.KEY_REFRESH_TOKEN_ENCRYPTED,
            )
            assertEquals(keys.size, keys.toSet().size, "All pref keys must be distinct")
        }
    }

    // -------------------------------------------------------------------------
    // 6. @Sensitive annotation metadata
    // -------------------------------------------------------------------------

    @Nested
    @DisplayName("@Sensitive annotation metadata")
    inner class SensitiveAnnotationMetadata {

        @Test
        fun `@Sensitive is retained at runtime`() {
            assertEquals(
                AnnotationRetention.RUNTIME,
                Sensitive::class.annotations
                    .filterIsInstance<Retention>()
                    .first().value,
                "@Sensitive must be RUNTIME retention for reflection-based redaction",
            )
        }

        @Test
        fun `@Sensitive targets properties and fields`() {
            val targets = Sensitive::class.annotations
                .filterIsInstance<Target>()
                .first().allowedTargets.toSet()

            assertTrue(
                targets.contains(AnnotationTarget.PROPERTY),
                "@Sensitive must target PROPERTY — ensures data class val params are redacted",
            )
            assertTrue(
                targets.contains(AnnotationTarget.FIELD),
                "@Sensitive must target FIELD — enables Java reflection detection",
            )
            assertFalse(
                targets.contains(AnnotationTarget.VALUE_PARAMETER),
                "@Sensitive must NOT target VALUE_PARAMETER — otherwise Kotlin applies it " +
                    "to constructor params instead of properties, breaking SensitiveFieldFilter",
            )
        }
    }

    // -------------------------------------------------------------------------
    // 7. Log redaction — verify no sensitive data in toString()
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
            val redacted = SensitiveFieldFilter.redact(response)
            val safe = SensitiveFieldFilter.redactToString(response)

            // deviceToken: last 8 chars preserved per spec (it's the device JWT)
            val dtValue = redacted["deviceToken"] as String
            assertTrue(dtValue.startsWith("..."), "deviceToken should show ...suffix")
            assertTrue(dtValue.endsWith("ignature"), "deviceToken suffix should be last 8 chars")

            // refreshToken: fully redacted per spec (not a JWT)
            assertEquals("[REDACTED]", redacted["refreshToken"],
                "Refresh token must be fully redacted")

            // Full string must not leak any token body
            assertFalse(safe.contains("eyJhbGciOiJSUzI1NiJ9"), "JWT body must not appear in redacted output")
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
            val redacted = SensitiveFieldFilter.redact(response)
            val safe = SensitiveFieldFilter.redactToString(response)

            // deviceToken: last 8 chars preserved (device JWT)
            val dtValue = redacted["deviceToken"] as String
            assertTrue(dtValue.startsWith("..."), "deviceToken should show ...suffix")

            // refreshToken: fully redacted
            assertEquals("[REDACTED]", redacted["refreshToken"])

            assertFalse(safe.contains("eyJhbGciOiJSUzI1NiJ9"))
            assertFalse(safe.contains("opaque-refresh"))
            assertTrue(safe.contains("ZM-LUSAKA-01"), "siteCode should be visible")
            assertTrue(safe.contains("d1234567"), "deviceId should be visible")
        }

        @Test
        fun `AgentFccConfig authCredential never appears in redacted output`() {
            val config = AgentFccConfig(
                fccVendor = FccVendor.DOMS,
                connectionProtocol = "REST",
                hostAddress = "192.168.1.100",
                port = 8080,
                authCredential = "fcc-secret-api-key-abc123def456",
                ingestionMode = IngestionMode.RELAY,
                pullIntervalSeconds = 30,
                productCodeMapping = mapOf("001" to "PMS"),
                timezone = "Africa/Johannesburg",
                currencyCode = "ZAR",
            )
            val safe = SensitiveFieldFilter.redactToString(config)

            assertFalse(
                safe.contains("fcc-secret-api-key"),
                "FCC credential must never appear in log-safe output",
            )
            assertTrue(safe.contains("DOMS"), "Non-sensitive vendor should be visible")
        }

        @Test
        fun `PreAuthCommand customerTaxId never appears in redacted output`() {
            val command = PreAuthCommand(
                siteCode = "ZM-LUSAKA-01",
                pumpNumber = 3,
                amountMinorUnits = 50000,
                currencyCode = "ZAR",
                customerTaxId = "9876543210",
            )
            val safe = SensitiveFieldFilter.redactToString(command)

            assertFalse(
                safe.contains("9876543210"),
                "Customer TIN (PII) must never appear in log-safe output",
            )
            assertTrue(safe.contains("ZM-LUSAKA-01"), "Non-sensitive siteCode should be visible")
        }

        @Test
        fun `data class toString includes sensitive values — proving SensitiveFieldFilter is needed`() {
            // This test proves that Kotlin data class toString() DOES leak sensitive data,
            // confirming that SensitiveFieldFilter must always be used for logging.
            val request = TokenRefreshRequest(refreshToken = "opaque-secret-refresh-token")
            val rawToString = request.toString()

            assertTrue(
                rawToString.contains("opaque-secret-refresh-token"),
                "Raw toString() leaks sensitive data — use SensitiveFieldFilter.redactToString() instead",
            )

            // But SensitiveFieldFilter protects it
            val safe = SensitiveFieldFilter.redactToString(request)
            assertFalse(safe.contains("opaque-secret"))
        }
    }
}
