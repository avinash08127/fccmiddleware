package com.fccmiddleware.edge.provisioning

import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudTokenRefreshResult
import com.fccmiddleware.edge.sync.KeystoreDeviceTokenProvider
import com.fccmiddleware.edge.sync.TokenRefreshResponse
import io.mockk.coEvery
import io.mockk.every
import io.mockk.mockk
import io.mockk.verify
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * Tests for [KeystoreDeviceTokenProvider].
 *
 * Uses Robolectric because the provider uses android.util.Base64 internally
 * for encoding/decoding Keystore-encrypted blobs.
 */
@RunWith(RobolectricTestRunner::class)
@Config(manifest = Config.NONE, sdk = [31])
class DeviceTokenProviderTest {

    private val keystoreManager = mockk<KeystoreManager>(relaxed = true)
    private val encryptedPrefs = mockk<EncryptedPrefsManager>(relaxed = true)
    private val cloudApiClient = mockk<CloudApiClient>()

    private lateinit var provider: KeystoreDeviceTokenProvider

    @Before
    fun setUp() {
        every { encryptedPrefs.isDecommissioned } returns false
        every { encryptedPrefs.storeTokenBlobs(any(), any()) } returns true
        provider = KeystoreDeviceTokenProvider(keystoreManager, encryptedPrefs, cloudApiClient)
    }

    // ── getAccessToken ──────────────────────────────────────────────────

    @Test
    fun `getAccessToken -- returns null when decommissioned`() {
        every { encryptedPrefs.isDecommissioned } returns true
        assertNull(provider.getAccessToken())
    }

    @Test
    fun `getAccessToken -- returns null when no token blob stored`() {
        every { encryptedPrefs.getDeviceTokenBlob() } returns null
        assertNull(provider.getAccessToken())
    }

    @Test
    fun `getAccessToken -- returns decrypted token from keystore`() {
        val fakeBlob = android.util.Base64.encodeToString(
            byteArrayOf(1, 2, 3),
            android.util.Base64.NO_WRAP,
        )
        every { encryptedPrefs.getDeviceTokenBlob() } returns fakeBlob
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_DEVICE_JWT, any())
        } returns "my-jwt-token"

        assertEquals("my-jwt-token", provider.getAccessToken())
    }

    // ── getLegalEntityId ────────────────────────────────────────────────

    @Test
    fun `getLegalEntityId -- returns value from encrypted prefs`() {
        every { encryptedPrefs.legalEntityId } returns "10000000-0000-0000-0000-000000000001"
        assertEquals("10000000-0000-0000-0000-000000000001", provider.getLegalEntityId())
    }

    @Test
    fun `getLegalEntityId -- returns null when not registered`() {
        every { encryptedPrefs.legalEntityId } returns null
        assertNull(provider.getLegalEntityId())
    }

    // ── refreshAccessToken ─────────────────────────────────────────────

    @Test
    fun `refreshAccessToken -- returns false when decommissioned`() = runTest {
        every { encryptedPrefs.isDecommissioned } returns true
        assertFalse(provider.refreshAccessToken())
    }

    @Test
    fun `refreshAccessToken -- returns false when no refresh token available`() = runTest {
        every { encryptedPrefs.getRefreshTokenBlob() } returns null
        assertFalse(provider.refreshAccessToken())
    }

    @Test
    fun `refreshAccessToken -- refreshes and stores new tokens on success`() = runTest {
        // Set up existing refresh token blob
        val refreshBlob = android.util.Base64.encodeToString(
            byteArrayOf(10, 20, 30),
            android.util.Base64.NO_WRAP,
        )
        every { encryptedPrefs.getRefreshTokenBlob() } returns refreshBlob
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, any())
        } returns "old-refresh-token"

        // FM-S03: refreshToken now requires both refresh token and device JWT
        every { encryptedPrefs.getDeviceTokenBlob() } returns android.util.Base64.encodeToString(
            byteArrayOf(4, 5, 6),
            android.util.Base64.NO_WRAP,
        )
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_DEVICE_JWT, any())
        } returns "current-device-jwt"

        coEvery { cloudApiClient.refreshToken("old-refresh-token", "current-device-jwt") } returns
            CloudTokenRefreshResult.Success(
                TokenRefreshResponse(
                    deviceToken = "new-jwt",
                    refreshToken = "new-refresh",
                    tokenExpiresAt = "2026-03-12T00:00:00Z",
                ),
            )

        every { keystoreManager.storeSecret(any(), any()) } returns byteArrayOf(1, 2, 3)

        assertTrue(provider.refreshAccessToken())

        verify { keystoreManager.storeSecret(KeystoreManager.ALIAS_DEVICE_JWT, "new-jwt") }
        verify { keystoreManager.storeSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, "new-refresh") }
        verify { encryptedPrefs.storeTokenBlobs(any(), any()) }
    }

    @Test
    fun `refreshAccessToken -- returns false on unauthorized (expired refresh token)`() = runTest {
        val refreshBlob = android.util.Base64.encodeToString(
            byteArrayOf(10, 20, 30),
            android.util.Base64.NO_WRAP,
        )
        every { encryptedPrefs.getRefreshTokenBlob() } returns refreshBlob
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, any())
        } returns "old-refresh"

        // FM-S03: Need device token blob for the refresh call
        every { encryptedPrefs.getDeviceTokenBlob() } returns android.util.Base64.encodeToString(
            byteArrayOf(4, 5, 6),
            android.util.Base64.NO_WRAP,
        )
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_DEVICE_JWT, any())
        } returns "expired-jwt"

        coEvery { cloudApiClient.refreshToken("old-refresh", "expired-jwt") } returns
            CloudTokenRefreshResult.Unauthorized

        assertFalse(provider.refreshAccessToken())
    }

    @Test
    fun `refreshAccessToken -- marks decommissioned on 403 DEVICE_DECOMMISSIONED`() = runTest {
        val refreshBlob = android.util.Base64.encodeToString(
            byteArrayOf(10, 20, 30),
            android.util.Base64.NO_WRAP,
        )
        every { encryptedPrefs.getRefreshTokenBlob() } returns refreshBlob
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, any())
        } returns "old-refresh"

        every { encryptedPrefs.getDeviceTokenBlob() } returns android.util.Base64.encodeToString(
            byteArrayOf(4, 5, 6),
            android.util.Base64.NO_WRAP,
        )
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_DEVICE_JWT, any())
        } returns "expired-jwt"

        coEvery { cloudApiClient.refreshToken("old-refresh", "expired-jwt") } returns
            CloudTokenRefreshResult.Forbidden("DEVICE_DECOMMISSIONED")

        assertFalse(provider.refreshAccessToken())
        verify { encryptedPrefs.isDecommissioned = true }
    }

    @Test
    fun `refreshAccessToken -- returns false on transport error`() = runTest {
        val refreshBlob = android.util.Base64.encodeToString(
            byteArrayOf(10, 20, 30),
            android.util.Base64.NO_WRAP,
        )
        every { encryptedPrefs.getRefreshTokenBlob() } returns refreshBlob
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, any())
        } returns "old-refresh"

        every { encryptedPrefs.getDeviceTokenBlob() } returns android.util.Base64.encodeToString(
            byteArrayOf(4, 5, 6),
            android.util.Base64.NO_WRAP,
        )
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_DEVICE_JWT, any())
        } returns "expired-jwt"

        coEvery { cloudApiClient.refreshToken("old-refresh", "expired-jwt") } returns
            CloudTokenRefreshResult.TransportError("timeout")

        assertFalse(provider.refreshAccessToken())
    }

    // ── isDecommissioned / markDecommissioned ──────────────────────────

    @Test
    fun `decommission -- isDecommissioned delegates to encrypted prefs`() {
        every { encryptedPrefs.isDecommissioned } returns false
        assertFalse(provider.isDecommissioned())

        every { encryptedPrefs.isDecommissioned } returns true
        assertTrue(provider.isDecommissioned())
    }

    @Test
    fun `decommission -- markDecommissioned sets flag in encrypted prefs`() {
        provider.markDecommissioned()
        verify { encryptedPrefs.isDecommissioned = true }
    }

    // ── storeTokens ────────────────────────────────────────────────────

    @Test
    fun `storeTokens -- encrypts and persists both tokens`() {
        every { keystoreManager.storeSecret(KeystoreManager.ALIAS_DEVICE_JWT, "jwt-value") } returns
            byteArrayOf(10, 20, 30)
        every { keystoreManager.storeSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, "refresh-value") } returns
            byteArrayOf(40, 50, 60)

        val result = provider.storeTokens("jwt-value", "refresh-value")

        assert(result) { "storeTokens should return true on success" }
        verify { encryptedPrefs.storeTokenBlobs(any(), any()) }
    }

    @Test
    fun `storeTokens -- returns false when atomic token persistence fails`() {
        every { keystoreManager.storeSecret(KeystoreManager.ALIAS_DEVICE_JWT, "jwt-value") } returns
            byteArrayOf(10, 20, 30)
        every { keystoreManager.storeSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, "refresh-value") } returns
            byteArrayOf(40, 50, 60)
        every { encryptedPrefs.storeTokenBlobs(any(), any()) } returns false

        val result = provider.storeTokens("jwt-value", "refresh-value")

        assertFalse(result)
        verify { encryptedPrefs.storeTokenBlobs(any(), any()) }
    }

    @Test
    fun `storeTokens -- returns false when device token encryption fails`() {
        every { keystoreManager.storeSecret(KeystoreManager.ALIAS_DEVICE_JWT, "jwt-value") } returns null

        val result = provider.storeTokens("jwt-value", "refresh-value")

        assert(!result) { "storeTokens should return false when device token encryption fails" }
        verify(exactly = 0) { encryptedPrefs.storeTokenBlobs(any(), any()) }
    }

    @Test
    fun `storeTokens -- returns false when refresh token encryption fails`() {
        every { keystoreManager.storeSecret(KeystoreManager.ALIAS_DEVICE_JWT, "jwt-value") } returns
            byteArrayOf(10, 20, 30)
        every { keystoreManager.storeSecret(KeystoreManager.ALIAS_REFRESH_TOKEN, "refresh-value") } returns null

        val result = provider.storeTokens("jwt-value", "refresh-value")

        assert(!result) { "storeTokens should return false when refresh token encryption fails" }
        verify(exactly = 0) { encryptedPrefs.storeTokenBlobs(any(), any()) }
    }
}
