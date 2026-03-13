package com.fccmiddleware.edge.registration

import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.config.LocalOverrideManager
import com.fccmiddleware.edge.config.SiteDataManager
import com.fccmiddleware.edge.config.canonicalEdgeConfigJson
import com.fccmiddleware.edge.security.EncryptedPrefsManager
import com.fccmiddleware.edge.security.KeystoreManager
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.DeviceRegistrationResponse
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Test

class RegistrationHandlerTest {

    private val cloudApiClient = mockk<CloudApiClient>(relaxed = true)
    private val keystoreManager = mockk<KeystoreManager>(relaxed = true)
    private val encryptedPrefs = mockk<EncryptedPrefsManager>(relaxed = true)
    private val agentConfigDao = mockk<AgentConfigDao>(relaxed = true)
    private val tokenProvider = mockk<DeviceTokenProvider>(relaxed = true)
    private val siteDataManager = mockk<SiteDataManager>(relaxed = true)
    private val bufferDatabase = mockk<BufferDatabase>(relaxed = true)
    private val localOverrideManager = mockk<LocalOverrideManager>(relaxed = true)

    private val handler = RegistrationHandler(
        cloudApiClient = cloudApiClient,
        keystoreManager = keystoreManager,
        encryptedPrefs = encryptedPrefs,
        agentConfigDao = agentConfigDao,
        tokenProvider = tokenProvider,
        siteDataManager = siteDataManager,
        bufferDatabase = bufferDatabase,
        localOverrideManager = localOverrideManager,
    )

    @Test
    fun `redactDeviceIdForLog truncates long ids to a stable prefix`() {
        assertEquals(
            "12345678...",
            RegistrationHandler.redactDeviceIdForLog("12345678-1234-1234-1234-123456789012"),
        )
        assertEquals("short-id", RegistrationHandler.redactDeviceIdForLog("short-id"))
    }

    @Test
    fun `completeRegistration skips Room config persistence when config encryption keeps failing`() = runTest {
        every { tokenProvider.storeTokens(any(), any()) } returns true
        every { encryptedPrefs.saveRegistration(any(), any(), any(), any(), any()) } returns true
        every { keystoreManager.storeSecret(KeystoreManager.ALIAS_CONFIG_INTEGRITY, any()) } returnsMany listOf(null, null)
        coEvery { siteDataManager.syncFromConfig(any()) } returns Unit

        val response = DeviceRegistrationResponse(
            deviceId = "11111111-2222-3333-4444-555555555555",
            deviceToken = "device-token",
            refreshToken = "refresh-token",
            tokenExpiresAt = "2026-03-13T00:00:00Z",
            siteCode = "SITE-001",
            legalEntityId = "22222222-2222-2222-2222-222222222222",
            siteConfig = Json.parseToJsonElement(canonicalEdgeConfigJson()).jsonObject,
            registeredAt = "2026-03-13T00:00:00Z",
        )

        handler.completeRegistration(
            qrCloudBaseUrl = "https://api.fccmiddleware.io",
            environment = "PRODUCTION",
            response = response,
        )

        coVerify(exactly = 0) { agentConfigDao.upsert(any()) }
        coVerify(exactly = 1) { siteDataManager.syncFromConfig(any()) }
    }
}
