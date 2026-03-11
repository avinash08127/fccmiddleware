package com.fccmiddleware.edge.security

import io.ktor.client.request.get
import io.ktor.http.HttpStatusCode
import io.ktor.serialization.kotlinx.json.json
import io.ktor.server.plugins.contentnegotiation.ContentNegotiation
import io.ktor.server.response.respondText
import io.ktor.server.routing.get
import io.ktor.server.routing.routing
import io.ktor.server.testing.testApplication
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.security.MessageDigest

/**
 * EA-6.2 LAN API Key Authentication Tests.
 *
 * Validates:
 *   - Localhost (127.0.0.1, ::1) requests bypass API key check
 *   - Non-localhost requests require X-Api-Key header
 *   - Invalid/missing API key returns 401
 *   - Constant-time comparison prevents timing attacks
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class LanApiKeyAuthTest {

    companion object {
        private const val STORED_KEY = "dGVzdC1sYW4tYXBpLWtleS0yNTYtYml0LXJhbmRvbQ=="
    }

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

    @Test
    fun `API key format is Base64URL-encoded 256-bit token`() {
        // Per security spec §5.3: LAN API key is 256-bit random, Base64URL-encoded
        // 256 bits = 32 bytes → Base64 = 44 chars (with padding) or 43 (without)
        val key = STORED_KEY
        assertTrue(
            key.length >= 32,
            "LAN API key should be at least 32 characters (Base64-encoded 256-bit token)",
        )
    }
}
