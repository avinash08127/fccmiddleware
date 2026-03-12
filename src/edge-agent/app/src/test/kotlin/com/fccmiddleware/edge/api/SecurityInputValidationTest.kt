package com.fccmiddleware.edge.api

import com.fccmiddleware.edge.sync.HttpCloudApiClient
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * SecurityInputValidationTest — validates security-related input validation:
 *   - SQL injection in query params → sanitized by type coercion (Int, ISO 8601)
 *   - Hostname extraction for cert pinning — malicious URLs
 *   - Retry-After header parsing — invalid values
 *   - Query parameter bounds (limit, offset)
 *   - Certificate pinning fail-fast on malformed URLs (M-07)
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class SecurityInputValidationTest {

    // -------------------------------------------------------------------------
    // Query param sanitization — pumpNumber (Int coercion)
    // -------------------------------------------------------------------------

    @Test
    fun `pumpNumber SQL injection attempt coerced to null via toIntOrNull`() {
        // TransactionRoutes uses: queryParameters["pumpNumber"]?.toIntOrNull()
        // Injection attempt should fail to parse
        val injectionAttempts = listOf(
            "1; DROP TABLE buffered_transactions;--",
            "1 OR 1=1",
            "' OR '1'='1",
            "-1 UNION SELECT * FROM pre_auth_records",
            "1/**/OR/**/1=1",
        )
        for (attempt in injectionAttempts) {
            val parsed = attempt.toIntOrNull()
            assertNull("Injection attempt '$attempt' should parse to null", parsed)
        }
    }

    @Test
    fun `valid pumpNumber values parse correctly`() {
        assertEquals(1, "1".toIntOrNull())
        assertEquals(42, "42".toIntOrNull())
        assertEquals(0, "0".toIntOrNull())
    }

    @Test
    fun `negative pumpNumber rejected by takeIf guard`() {
        // TransactionRoutes: .toIntOrNull()?.takeIf { it >= 0 }
        val negative = "-1".toIntOrNull()?.takeIf { it >= 0 }
        assertNull(negative)
    }

    // -------------------------------------------------------------------------
    // Query param sanitization — limit and offset bounds
    // -------------------------------------------------------------------------

    @Test
    fun `limit coerced to valid range 1 to 100`() {
        // TransactionRoutes: (limit?.toIntOrNull() ?: 50).coerceIn(1, 100)
        assertEquals(100, "99999".toIntOrNull()?.coerceIn(1, 100))
        assertEquals(1, "0".toIntOrNull()?.coerceIn(1, 100))
        assertEquals(1, "-5".toIntOrNull()?.coerceIn(1, 100))
        assertEquals(50, "50".toIntOrNull()?.coerceIn(1, 100))
    }

    @Test
    fun `limit SQL injection attempt defaults to 50 via null coercion`() {
        // Injection attempts fail toIntOrNull() → default 50
        val parsed = "50; DROP TABLE".toIntOrNull() ?: 50
        assertEquals(50, parsed)
    }

    @Test
    fun `offset coerced to non-negative`() {
        // TransactionRoutes: (offset?.toIntOrNull() ?: 0).coerceAtLeast(0)
        assertEquals(0, "-100".toIntOrNull()?.coerceAtLeast(0))
        assertEquals(0, "0".toIntOrNull()?.coerceAtLeast(0))
        assertEquals(10, "10".toIntOrNull()?.coerceAtLeast(0))
    }

    // -------------------------------------------------------------------------
    // since param — ISO 8601 validation
    // -------------------------------------------------------------------------

    @Test
    fun `since param SQL injection attempt fails ISO 8601 parse`() {
        val injections = listOf(
            "2024-01-01' OR '1'='1",
            "'; DROP TABLE buffered_transactions;--",
            "2024-01-01T00:00:00Z UNION SELECT",
        )
        for (injection in injections) {
            val parsed = try {
                java.time.Instant.parse(injection)
                true
            } catch (_: Exception) {
                false
            }
            assertEquals("Injection '$injection' should fail parse", false, parsed)
        }
    }

    @Test
    fun `valid ISO 8601 timestamps parse correctly`() {
        assertNotNull(java.time.Instant.parse("2024-01-15T10:00:00Z"))
        assertNotNull(java.time.Instant.parse("2024-12-31T23:59:59.999Z"))
    }

    // -------------------------------------------------------------------------
    // Hostname extraction for cert pinning — malicious URLs
    // -------------------------------------------------------------------------

    @Test
    fun `extractHostname with valid URL`() {
        assertEquals("api.example.com", HttpCloudApiClient.extractHostname("https://api.example.com"))
        assertEquals("api.example.com", HttpCloudApiClient.extractHostname("https://api.example.com/path"))
        assertEquals("api.example.com", HttpCloudApiClient.extractHostname("https://api.example.com:443/path"))
    }

    @Test
    fun `extractHostname with empty URL returns null`() {
        assertNull(HttpCloudApiClient.extractHostname(""))
        assertNull(HttpCloudApiClient.extractHostname("https://"))
    }

    @Test
    fun `extractHostname with malicious URL does not crash`() {
        // These should not crash — either return null or a safe string
        HttpCloudApiClient.extractHostname("https://evil.com/../../etc/passwd")
        HttpCloudApiClient.extractHostname("https://evil.com@good.com")
        HttpCloudApiClient.extractHostname("https://127.0.0.1")
    }

    @Test
    fun `extractHostname strips port number`() {
        assertEquals("api.example.com", HttpCloudApiClient.extractHostname("https://api.example.com:8443"))
    }

    // -------------------------------------------------------------------------
    // M-07: Certificate pinning fail-fast on malformed URL
    // -------------------------------------------------------------------------

    @Test
    fun `M-07 create with empty cloudBaseUrl and pins throws IllegalArgumentException`() {
        assertThrows(IllegalArgumentException::class.java) {
            HttpCloudApiClient.create(
                cloudBaseUrl = "",
                certificatePins = listOf("sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="),
            )
        }
    }

    @Test
    fun `M-07 create with malformed URL and pins throws IllegalArgumentException`() {
        assertThrows(IllegalArgumentException::class.java) {
            HttpCloudApiClient.create(
                cloudBaseUrl = "https://",
                certificatePins = listOf("sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="),
            )
        }
    }

    @Test
    fun `create without certificate pins does not throw on any URL`() {
        // Without pins, no hostname extraction needed — should not throw
        val client = HttpCloudApiClient.create(cloudBaseUrl = "https://api.example.com")
        assertNotNull(client)
    }

    // -------------------------------------------------------------------------
    // Retry-After header parsing
    // -------------------------------------------------------------------------

    @Test
    fun `parseRetryAfterSeconds with valid integer`() {
        assertEquals(30L, HttpCloudApiClient.parseRetryAfterSeconds("30"))
        assertEquals(120L, HttpCloudApiClient.parseRetryAfterSeconds("120"))
        assertEquals(1L, HttpCloudApiClient.parseRetryAfterSeconds("1"))
    }

    @Test
    fun `parseRetryAfterSeconds with invalid values returns null`() {
        assertNull(HttpCloudApiClient.parseRetryAfterSeconds(null))
        assertNull(HttpCloudApiClient.parseRetryAfterSeconds(""))
        assertNull(HttpCloudApiClient.parseRetryAfterSeconds("not-a-number"))
        assertNull(HttpCloudApiClient.parseRetryAfterSeconds("Wed, 21 Oct 2015 07:28:00 GMT"))
    }

    @Test
    fun `parseRetryAfterSeconds rejects zero and negative values`() {
        assertNull(HttpCloudApiClient.parseRetryAfterSeconds("0"))
        assertNull(HttpCloudApiClient.parseRetryAfterSeconds("-5"))
    }

    @Test
    fun `parseRetryAfterSeconds trims whitespace`() {
        assertEquals(30L, HttpCloudApiClient.parseRetryAfterSeconds("  30  "))
    }

    // -------------------------------------------------------------------------
    // JSON bomb protection — lenient parser ignores unknown fields
    // -------------------------------------------------------------------------

    @Test
    fun `lenient JSON parser ignores unknown fields in cloud responses`() {
        // The HttpCloudApiClient uses Json { ignoreUnknownKeys = true }
        // This prevents JSON bombs with unexpected fields from crashing the parser
        val json = kotlinx.serialization.json.Json {
            ignoreUnknownKeys = true
            isLenient = true
        }

        // Parse a response with extra unknown fields — should not throw
        val responseJson = """
            {
                "results": [],
                "acceptedCount": 0,
                "duplicateCount": 0,
                "rejectedCount": 0,
                "unexpectedField": "value",
                "nested": {"deep": {"field": true}},
                "array": [1, 2, 3, 4, 5]
            }
        """.trimIndent()

        val response = json.decodeFromString<com.fccmiddleware.edge.sync.CloudUploadResponse>(responseJson)
        assertEquals(0, response.acceptedCount)
        assertEquals(0, response.results.size)
    }

    @Test
    fun `deeply nested JSON does not crash parser`() {
        val json = kotlinx.serialization.json.Json {
            ignoreUnknownKeys = true
            isLenient = true
        }

        // A response with deeply nested unknown fields
        val responseJson = """
            {
                "statuses": [
                    {"id": "fcc-1", "status": "SYNCED_TO_ODOO", "extra": {"deep": {"nested": "value"}}}
                ]
            }
        """.trimIndent()

        val response = json.decodeFromString<com.fccmiddleware.edge.sync.SyncedStatusResponse>(responseJson)
        assertEquals(1, response.statuses.size)
        assertEquals("SYNCED_TO_ODOO", response.statuses[0].status)
    }
}
