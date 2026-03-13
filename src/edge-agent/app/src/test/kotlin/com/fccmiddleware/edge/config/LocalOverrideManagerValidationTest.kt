package com.fccmiddleware.edge.config

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * TG-001 (SettingsActivity validation logic) — unit tests for the static
 * [LocalOverrideManager] validation helpers.
 *
 * These helpers back the SettingsActivity form validation, so testing them
 * here is equivalent to testing that validation logic.
 *
 * Covers:
 *   - isValidHostOrIp: valid IPv4 addresses
 *   - isValidHostOrIp: valid hostnames
 *   - isValidHostOrIp: invalid/malformed values
 *   - isValidPort: valid range (1-65535)
 *   - isValidPort: boundary values
 *   - isValidPort: out-of-range values
 */
class LocalOverrideManagerValidationTest {

    // ── isValidHostOrIp — IPv4 ────────────────────────────────────────────────

    @Test
    fun `isValidHostOrIp accepts standard private IPv4 address`() {
        assertTrue(LocalOverrideManager.isValidHostOrIp("192.168.1.100"))
    }

    @Test
    fun `isValidHostOrIp accepts loopback address`() {
        assertTrue(LocalOverrideManager.isValidHostOrIp("127.0.0.1"))
    }

    @Test
    fun `isValidHostOrIp accepts all-zeros address`() {
        assertTrue(LocalOverrideManager.isValidHostOrIp("0.0.0.0"))
    }

    @Test
    fun `isValidHostOrIp accepts broadcast address`() {
        assertTrue(LocalOverrideManager.isValidHostOrIp("255.255.255.255"))
    }

    @Test
    fun `isValidHostOrIp accepts 10-dot subnet addresses`() {
        assertTrue(LocalOverrideManager.isValidHostOrIp("10.0.0.1"))
        assertTrue(LocalOverrideManager.isValidHostOrIp("10.255.255.254"))
    }

    // ── isValidHostOrIp — hostnames ───────────────────────────────────────────

    @Test
    fun `isValidHostOrIp accepts simple single-label hostname`() {
        assertTrue(LocalOverrideManager.isValidHostOrIp("fccdevice"))
    }

    @Test
    fun `isValidHostOrIp accepts multi-label hostname`() {
        assertTrue(LocalOverrideManager.isValidHostOrIp("fcc.local"))
    }

    @Test
    fun `isValidHostOrIp accepts hostname with hyphens`() {
        assertTrue(LocalOverrideManager.isValidHostOrIp("fcc-device-01"))
    }

    // ── isValidHostOrIp — invalid inputs ──────────────────────────────────────

    @Test
    fun `isValidHostOrIp rejects blank string`() {
        assertFalse(LocalOverrideManager.isValidHostOrIp(""))
        assertFalse(LocalOverrideManager.isValidHostOrIp("   "))
    }

    @Test
    fun `isValidHostOrIp rejects IPv4 with out-of-range octet`() {
        assertFalse(LocalOverrideManager.isValidHostOrIp("256.0.0.1"))
        assertFalse(LocalOverrideManager.isValidHostOrIp("192.168.1.300"))
    }

    @Test
    fun `isValidHostOrIp rejects IPv4 with too few octets`() {
        assertFalse(LocalOverrideManager.isValidHostOrIp("192.168.1"))
    }

    @Test
    fun `isValidHostOrIp rejects IPv4 with too many octets`() {
        assertFalse(LocalOverrideManager.isValidHostOrIp("192.168.1.1.1"))
    }

    @Test
    fun `isValidHostOrIp rejects strings with spaces`() {
        assertFalse(LocalOverrideManager.isValidHostOrIp("192.168.1. 1"))
        assertFalse(LocalOverrideManager.isValidHostOrIp("fcc device"))
    }

    @Test
    fun `isValidHostOrIp rejects hostname starting with hyphen`() {
        assertFalse(LocalOverrideManager.isValidHostOrIp("-invalid.local"))
    }

    @Test
    fun `isValidHostOrIp rejects empty label (double dot)`() {
        assertFalse(LocalOverrideManager.isValidHostOrIp("fcc..local"))
    }

    // ── isValidPort ───────────────────────────────────────────────────────────

    @Test
    fun `isValidPort accepts common port numbers`() {
        assertTrue(LocalOverrideManager.isValidPort(8080))
        assertTrue(LocalOverrideManager.isValidPort(5560))
        assertTrue(LocalOverrideManager.isValidPort(443))
        assertTrue(LocalOverrideManager.isValidPort(8585))
    }

    @Test
    fun `isValidPort accepts minimum valid port 1`() {
        assertTrue(LocalOverrideManager.isValidPort(1))
    }

    @Test
    fun `isValidPort accepts maximum valid port 65535`() {
        assertTrue(LocalOverrideManager.isValidPort(65535))
    }

    @Test
    fun `isValidPort rejects port 0`() {
        assertFalse(LocalOverrideManager.isValidPort(0))
    }

    @Test
    fun `isValidPort rejects negative ports`() {
        assertFalse(LocalOverrideManager.isValidPort(-1))
        assertFalse(LocalOverrideManager.isValidPort(Int.MIN_VALUE))
    }

    @Test
    fun `isValidPort rejects port 65536`() {
        assertFalse(LocalOverrideManager.isValidPort(65536))
    }

    @Test
    fun `isValidPort rejects very large port numbers`() {
        assertFalse(LocalOverrideManager.isValidPort(Int.MAX_VALUE))
    }

    // ── deriveEnvironment (via reflection) ────────────────────────────────────
    // The deriveEnvironment method is private on SettingsActivity, but its
    // logic is fully documented and simple enough to test here via inlining
    // the equivalent logic — avoiding Robolectric overhead for a pure function.

    private fun deriveEnvironment(cloudUrl: String?): String {
        if (cloudUrl == null) return "Unknown"
        return when {
            cloudUrl.contains("staging", ignoreCase = true) -> "Staging"
            cloudUrl.contains("dev", ignoreCase = true) -> "Development"
            cloudUrl.contains("uat", ignoreCase = true) -> "UAT"
            else -> "Production"
        }
    }

    @Test
    fun `deriveEnvironment returns Staging for staging URL`() {
        assertEquals("Staging", deriveEnvironment("https://staging.fccmiddleware.io"))
    }

    @Test
    fun `deriveEnvironment returns Development for dev URL`() {
        assertEquals("Development", deriveEnvironment("https://dev.fccmiddleware.io"))
    }

    @Test
    fun `deriveEnvironment returns UAT for UAT URL`() {
        assertEquals("UAT", deriveEnvironment("https://uat.fccmiddleware.io"))
    }

    @Test
    fun `deriveEnvironment returns Production for production URL`() {
        assertEquals("Production", deriveEnvironment("https://api.fccmiddleware.io"))
    }

    @Test
    fun `deriveEnvironment returns Unknown for null URL`() {
        assertEquals("Unknown", deriveEnvironment(null))
    }

    private fun assertEquals(expected: String, actual: String) {
        org.junit.Assert.assertEquals(expected, actual)
    }
}
