package com.fccmiddleware.edge.websocket

import com.fccmiddleware.edge.config.WebSocketDto
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class OdooWebSocketServerTest {

    @Test
    fun `WebSocketDto defaults to loopback LAN auth and 64 KiB max frame size`() {
        val config = WebSocketDto()

        assertEquals("127.0.0.1", config.bindAddress)
        assertTrue(config.requireApiKeyForLan)
        assertEquals(64, config.maxFrameSizeKb)
    }

    @Test
    fun `webSocketSharedSecretEquals returns true for matching secrets`() {
        assertTrue(webSocketSharedSecretEquals("shared-secret", "shared-secret"))
    }

    @Test
    fun `webSocketSharedSecretEquals returns false for mismatched secrets`() {
        assertFalse(webSocketSharedSecretEquals("shared-secret", "shared-secret-2"))
    }

    @Test
    fun `webSocketSharedSecretEquals returns false when header is missing`() {
        assertFalse(webSocketSharedSecretEquals(null, "shared-secret"))
    }

    @Test
    fun `resolveWebSocketMaxFrameSizeBytes converts kibibytes to bytes with floor of one kibibyte`() {
        assertEquals(64L * 1024L, resolveWebSocketMaxFrameSizeBytes(64))
        assertEquals(1024L, resolveWebSocketMaxFrameSizeBytes(0))
    }

    @Test
    fun `isLoopbackHost recognizes localhost and rejects LAN addresses`() {
        assertTrue(isLoopbackHost("127.0.0.1"))
        assertTrue(isLoopbackHost("localhost"))
        assertFalse(isLoopbackHost("192.168.1.10"))
    }

    @Test
    fun `shouldRequireApiKeyForLan only applies to non-loopback clients when enabled`() {
        val enabled = WebSocketDto(requireApiKeyForLan = true)
        val disabled = WebSocketDto(requireApiKeyForLan = false)

        assertTrue(shouldRequireApiKeyForLan(enabled, "192.168.1.10"))
        assertFalse(shouldRequireApiKeyForLan(enabled, "127.0.0.1"))
        assertFalse(shouldRequireApiKeyForLan(disabled, "192.168.1.10"))
    }

    @Test
    fun `requiresAuthenticatedSession is true for mutating websocket commands`() {
        assertTrue(requiresAuthenticatedSession("manager_update"))
        assertTrue(requiresAuthenticatedSession("attendant_update"))
        assertTrue(requiresAuthenticatedSession("manager_manual_update"))
        assertTrue(requiresAuthenticatedSession("fp_unblock"))
        assertFalse(requiresAuthenticatedSession("latest"))
        assertFalse(requiresAuthenticatedSession("all"))
    }
}
