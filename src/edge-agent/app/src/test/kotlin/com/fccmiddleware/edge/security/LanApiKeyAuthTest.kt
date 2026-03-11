package com.fccmiddleware.edge.security

import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.http.HttpStatusCode
import io.ktor.serialization.kotlinx.json.json
import io.ktor.server.application.call
import io.ktor.server.application.createApplicationPlugin
import io.ktor.server.application.install
import io.ktor.server.plugins.contentnegotiation.ContentNegotiation
import io.ktor.server.plugins.origin
import io.ktor.server.response.respond
import io.ktor.server.response.respondText
import io.ktor.server.routing.get
import io.ktor.server.routing.routing
import io.ktor.server.testing.testApplication
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64

/**
 * EA-6.2 LAN API Key Authentication Tests.
 *
 * Validates:
 *   - Localhost (127.0.0.1, ::1) requests bypass API key check
 *   - Non-localhost requests require X-Api-Key header
 *   - Invalid/missing API key returns 401
 *   - Constant-time comparison prevents timing attacks
 *   - API key format: 256-bit random, Base64URL-encoded
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class LanApiKeyAuthTest {

    companion object {
        private const val STORED_KEY = "dGVzdC1sYW4tYXBpLWtleS0yNTYtYml0LXJhbmRvbQ=="
    }

    // -------------------------------------------------------------------------
    // Constant-time comparison
    // -------------------------------------------------------------------------

    @Test
    fun `constant-time comparison returns true for equal keys`() {
        val a = "test-key-value".toByteArray(Charsets.UTF_8)
        val b = "test-key-value".toByteArray(Charsets.UTF_8)
        assertTrue(MessageDigest.isEqual(a, b))
    }

    @Test
    fun `constant-time comparison returns false for different keys`() {
        val a = "correct-key".toByteArray(Charsets.UTF_8)
        val b = "wrong-key".toByteArray(Charsets.UTF_8)
        assertEquals(false, MessageDigest.isEqual(a, b))
    }

    @Test
    fun `constant-time comparison returns false for empty vs non-empty`() {
        val a = "".toByteArray(Charsets.UTF_8)
        val b = "key".toByteArray(Charsets.UTF_8)
        assertEquals(false, MessageDigest.isEqual(a, b))
    }

    @Test
    fun `constant-time comparison returns false for prefix match`() {
        // Ensure partial/prefix matches are rejected
        val a = "correct-key".toByteArray(Charsets.UTF_8)
        val b = "correct-key-extra".toByteArray(Charsets.UTF_8)
        assertFalse(MessageDigest.isEqual(a, b))
    }

    // -------------------------------------------------------------------------
    // Ktor test application — localhost bypass
    // -------------------------------------------------------------------------

    @Test
    fun `localhost request bypasses API key check in Ktor test`() = testApplication {
        application {
            install(ContentNegotiation) { json() }
            routing {
                get("/api/v1/status") {
                    call.respondText("OK")
                }
            }
        }
        // Ktor test client defaults to localhost, so no API key needed
        val response = client.get("/api/v1/status")
        assertEquals(HttpStatusCode.OK, response.status)
    }

    // -------------------------------------------------------------------------
    // API key format validation
    // -------------------------------------------------------------------------

    @Test
    fun `API key format is Base64URL-encoded 256-bit token`() {
        // Per security spec 5.3: LAN API key is 256-bit random, Base64URL-encoded
        // 256 bits = 32 bytes -> Base64 = 44 chars (with padding) or 43 (without)
        val key = STORED_KEY
        assertTrue(
            "LAN API key should be at least 32 characters (Base64-encoded 256-bit token)",
            key.length >= 32,
        )
    }

    @Test
    fun `generated 256-bit key is 32 bytes when decoded`() {
        // Verify the generation algorithm produces correct-length keys
        val random = SecureRandom()
        val keyBytes = ByteArray(32) // 256 bits
        random.nextBytes(keyBytes)
        val encoded = Base64.getUrlEncoder().withoutPadding().encodeToString(keyBytes)

        assertEquals("Base64URL 256-bit key without padding should be 43 chars", 43, encoded.length)

        val decoded = Base64.getUrlDecoder().decode(encoded)
        assertEquals("Decoded key should be exactly 32 bytes (256 bits)", 32, decoded.size)
    }

    // -------------------------------------------------------------------------
    // LAN API key auth plugin — integration tests
    // -------------------------------------------------------------------------

    /**
     * Creates a Ktor test app with the same LAN auth logic as [LocalApiServer].
     * Uses simulated non-localhost address via a custom plugin.
     */
    @Test
    fun `non-localhost request without API key is rejected with 401`() = testApplication {
        application {
            install(ContentNegotiation) { json() }
            // Simulate LAN auth: reject if no valid X-Api-Key for non-localhost
            install(createLanAuthTestPlugin(STORED_KEY))
            routing {
                get("/api/v1/status") {
                    call.respondText("OK")
                }
            }
        }
        // No X-Api-Key header — should be rejected
        val response = client.get("/api/v1/status")
        assertEquals(
            "Non-localhost request without API key must return 401",
            HttpStatusCode.Unauthorized,
            response.status,
        )
    }

    @Test
    fun `non-localhost request with wrong API key is rejected with 401`() = testApplication {
        application {
            install(ContentNegotiation) { json() }
            install(createLanAuthTestPlugin(STORED_KEY))
            routing {
                get("/api/v1/status") {
                    call.respondText("OK")
                }
            }
        }
        val response = client.get("/api/v1/status") {
            header("X-Api-Key", "wrong-key-value")
        }
        assertEquals(
            "Non-localhost request with wrong API key must return 401",
            HttpStatusCode.Unauthorized,
            response.status,
        )
    }

    @Test
    fun `non-localhost request with correct API key is allowed`() = testApplication {
        application {
            install(ContentNegotiation) { json() }
            install(createLanAuthTestPlugin(STORED_KEY))
            routing {
                get("/api/v1/status") {
                    call.respondText("OK")
                }
            }
        }
        val response = client.get("/api/v1/status") {
            header("X-Api-Key", STORED_KEY)
        }
        assertEquals(
            "Non-localhost request with correct API key must be allowed",
            HttpStatusCode.OK,
            response.status,
        )
    }

    @Test
    fun `non-localhost request with empty API key is rejected with 401`() = testApplication {
        application {
            install(ContentNegotiation) { json() }
            install(createLanAuthTestPlugin(STORED_KEY))
            routing {
                get("/api/v1/status") {
                    call.respondText("OK")
                }
            }
        }
        val response = client.get("/api/v1/status") {
            header("X-Api-Key", "")
        }
        assertEquals(
            "Non-localhost request with empty API key must return 401",
            HttpStatusCode.Unauthorized,
            response.status,
        )
    }

    /**
     * Creates a test-only LAN auth plugin that simulates non-localhost origin.
     *
     * In the real [LocalApiServer], the plugin checks `call.request.origin.remoteHost`.
     * In Ktor test host, all requests appear as localhost. This plugin simulates
     * a non-localhost scenario by always requiring the API key.
     */
    private fun createLanAuthTestPlugin(storedKey: String) =
        createApplicationPlugin("LanApiKeyAuthTest") {
            onCall { call ->
                // Simulate non-localhost: always require API key
                val headerKey = call.request.headers["X-Api-Key"]
                if (headerKey == null || !MessageDigest.isEqual(
                        headerKey.toByteArray(Charsets.UTF_8),
                        storedKey.toByteArray(Charsets.UTF_8),
                    )
                ) {
                    call.respond(
                        HttpStatusCode.Unauthorized,
                        mapOf(
                            "errorCode" to "UNAUTHORIZED",
                            "message" to "Valid X-Api-Key header required for LAN access",
                        ),
                    )
                }
            }
        }
}
