package com.fccmiddleware.edge.provisioning

import com.fccmiddleware.edge.ui.QrBootstrapData
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonPrimitive
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.DisplayName
import org.junit.jupiter.api.Nested
import org.junit.jupiter.api.Test

/**
 * Tests for QR code payload parsing logic.
 * Schema: { "v": 1, "sc": "SITE-CODE", "cu": "https://...", "pt": "token" }
 */
class QrPayloadParsingTest {

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
    }

    /**
     * Standalone parser matching ProvisioningActivity.parseQrPayload logic.
     * Extracted here for testability without Android Activity dependencies.
     */
    private fun parseQrPayload(rawJson: String): QrBootstrapData? {
        return try {
            val obj = json.decodeFromString<JsonObject>(rawJson)
            val version = obj["v"]?.jsonPrimitive?.int
            val siteCode = obj["sc"]?.jsonPrimitive?.content
            val cloudUrl = obj["cu"]?.jsonPrimitive?.content
            val token = obj["pt"]?.jsonPrimitive?.content

            if (version == null || version != 1) return null
            if (siteCode.isNullOrBlank() || cloudUrl.isNullOrBlank() || token.isNullOrBlank()) return null

            QrBootstrapData(
                siteCode = siteCode,
                cloudBaseUrl = cloudUrl.trimEnd('/'),
                provisioningToken = token,
            )
        } catch (_: Exception) {
            null
        }
    }

    @Nested
    @DisplayName("Valid QR payloads")
    inner class ValidPayloads {

        @Test
        fun `parses standard QR payload`() {
            val payload = """{"v":1,"sc":"MW-LLW-001","cu":"https://api.fccmiddleware.io","pt":"dGVzdC10b2tlbi1iYXNlNjR1cmw"}"""
            val result = parseQrPayload(payload)

            assertNotNull(result)
            assertEquals("MW-LLW-001", result!!.siteCode)
            assertEquals("https://api.fccmiddleware.io", result.cloudBaseUrl)
            assertEquals("dGVzdC10b2tlbi1iYXNlNjR1cmw", result.provisioningToken)
        }

        @Test
        fun `strips trailing slash from cloudBaseUrl`() {
            val payload = """{"v":1,"sc":"SITE-A","cu":"https://api.example.com/","pt":"token123"}"""
            val result = parseQrPayload(payload)

            assertNotNull(result)
            assertEquals("https://api.example.com", result!!.cloudBaseUrl)
        }

        @Test
        fun `ignores extra fields in QR payload`() {
            val payload = """{"v":1,"sc":"SITE-B","cu":"https://api.example.com","pt":"tok","extra":"ignored"}"""
            val result = parseQrPayload(payload)

            assertNotNull(result)
            assertEquals("SITE-B", result!!.siteCode)
        }

        @Test
        fun `handles hyphenated site codes`() {
            val payload = """{"v":1,"sc":"TZ-DAR-002","cu":"https://api.fcc.io","pt":"abc123"}"""
            val result = parseQrPayload(payload)

            assertNotNull(result)
            assertEquals("TZ-DAR-002", result!!.siteCode)
        }
    }

    @Nested
    @DisplayName("Invalid QR payloads")
    inner class InvalidPayloads {

        @Test
        fun `rejects unsupported version`() {
            val payload = """{"v":2,"sc":"SITE","cu":"https://api.com","pt":"tok"}"""
            assertNull(parseQrPayload(payload))
        }

        @Test
        fun `rejects missing version`() {
            val payload = """{"sc":"SITE","cu":"https://api.com","pt":"tok"}"""
            assertNull(parseQrPayload(payload))
        }

        @Test
        fun `rejects missing siteCode`() {
            val payload = """{"v":1,"cu":"https://api.com","pt":"tok"}"""
            assertNull(parseQrPayload(payload))
        }

        @Test
        fun `rejects missing cloudBaseUrl`() {
            val payload = """{"v":1,"sc":"SITE","pt":"tok"}"""
            assertNull(parseQrPayload(payload))
        }

        @Test
        fun `rejects missing provisioningToken`() {
            val payload = """{"v":1,"sc":"SITE","cu":"https://api.com"}"""
            assertNull(parseQrPayload(payload))
        }

        @Test
        fun `rejects blank siteCode`() {
            val payload = """{"v":1,"sc":"","cu":"https://api.com","pt":"tok"}"""
            assertNull(parseQrPayload(payload))
        }

        @Test
        fun `rejects blank provisioningToken`() {
            val payload = """{"v":1,"sc":"SITE","cu":"https://api.com","pt":""}"""
            assertNull(parseQrPayload(payload))
        }

        @Test
        fun `rejects non-JSON input`() {
            assertNull(parseQrPayload("not json at all"))
        }

        @Test
        fun `rejects empty string`() {
            assertNull(parseQrPayload(""))
        }

        @Test
        fun `rejects JSON array`() {
            assertNull(parseQrPayload("""[1,2,3]"""))
        }
    }
}
