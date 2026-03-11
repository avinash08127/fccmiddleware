package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.config.ConfigApplyResult
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.EdgeAgentConfigDto
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import io.mockk.slot
import io.mockk.verify
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant

/**
 * ConfigPollWorkerTest — unit tests for EA-3.3 Config Poll Worker.
 *
 * Validates:
 *   - No-ops when dependencies are null
 *   - No-ops when device is decommissioned
 *   - No-ops when backoff is active
 *   - No-ops when access token is unavailable
 *   - 304 Not Modified → no config apply, lastConfigPullAt updated
 *   - 200 OK → config parsed and applied via ConfigManager
 *   - Applied config → SyncState updated with configVersion
 *   - Skipped config → lastConfigPullAt updated but version unchanged
 *   - Rejected config → lastConfigPullAt updated but version unchanged
 *   - 401 → token refresh + retry → success
 *   - 401 → refresh fails → transport failure with backoff
 *   - 403 DEVICE_DECOMMISSIONED → markDecommissioned()
 *   - Transport error → failure count increases, backoff applied
 *   - Successful poll after failures resets backoff
 *   - Malformed JSON → failure with backoff
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class ConfigPollWorkerTest {

    private val configManager: ConfigManager = mockk(relaxed = true)
    private val syncStateDao: SyncStateDao = mockk(relaxed = true)
    private val cloudApiClient: CloudApiClient = mockk()
    private val tokenProvider: DeviceTokenProvider = mockk()

    private lateinit var worker: ConfigPollWorker

    companion object {
        private const val VALID_TOKEN = "valid-jwt-token"
        private const val VALID_CONFIG_JSON = """
        {
            "schemaVersion": "2.0",
            "configVersion": 5,
            "configId": "00000000-0000-0000-0000-000000000001",
            "issuedAtUtc": "2025-01-01T00:00:00Z",
            "effectiveAtUtc": "2025-01-01T00:00:00Z",
            "compatibility": { "minAgentVersion": "1.0.0" },
            "agent": { "deviceId": "11111111-1111-1111-1111-111111111111", "isPrimaryAgent": true },
            "site": {
                "siteCode": "SITE-001",
                "legalEntityId": "22222222-2222-2222-2222-222222222222",
                "timezone": "Africa/Johannesburg",
                "currency": "ZAR",
                "operatingModel": "COCO",
                "connectivityMode": "CONNECTED"
            },
            "fccConnection": {
                "vendor": "DOMS",
                "host": "192.168.1.100",
                "port": 8080,
                "credentialsRef": "fcc/site-001",
                "protocolType": "REST",
                "transactionMode": "PULL",
                "ingestionMode": "RELAY",
                "heartbeatIntervalSeconds": 15
            },
            "polling": { "pullIntervalSeconds": 30, "batchSize": 100, "cursorStrategy": "LAST_SUCCESSFUL_TIMESTAMP" },
            "sync": {
                "cloudBaseUrl": "https://api.fccmiddleware.io",
                "uploadBatchSize": 50,
                "syncIntervalSeconds": 30,
                "statusPollIntervalSeconds": 30,
                "configPollIntervalSeconds": 60
            },
            "buffer": { "retentionDays": 30, "maxRecords": 50000, "cleanupIntervalHours": 24 },
            "api": { "localApiPort": 8585, "enableLanApi": false },
            "telemetry": { "telemetryIntervalSeconds": 60, "logLevel": "INFO" },
            "fiscalization": { "mode": "NONE", "requireCustomerTaxId": false, "fiscalReceiptRequired": false }
        }
        """
    }

    @Before
    fun setUp() {
        worker = ConfigPollWorker(
            configManager = configManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )
        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns VALID_TOKEN
        every { configManager.currentConfigVersion } returns 4
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit
    }

    // -------------------------------------------------------------------------
    // No-op guards
    // -------------------------------------------------------------------------

    @Test
    fun `returns early when configManager is null`() = runTest {
        val w = ConfigPollWorker(
            configManager = null,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )
        w.pollConfig()
        coVerify(exactly = 0) { cloudApiClient.getConfig(any(), any()) }
    }

    @Test
    fun `returns early when cloudApiClient is null`() = runTest {
        val w = ConfigPollWorker(
            configManager = configManager,
            syncStateDao = syncStateDao,
            cloudApiClient = null,
            tokenProvider = tokenProvider,
        )
        w.pollConfig()
        coVerify(exactly = 0) { configManager.applyConfig(any(), any()) }
    }

    @Test
    fun `returns early when tokenProvider is null`() = runTest {
        val w = ConfigPollWorker(
            configManager = configManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = null,
        )
        w.pollConfig()
        coVerify(exactly = 0) { cloudApiClient.getConfig(any(), any()) }
    }

    @Test
    fun `returns early when device is decommissioned`() = runTest {
        every { tokenProvider.isDecommissioned() } returns true
        worker.pollConfig()
        coVerify(exactly = 0) { cloudApiClient.getConfig(any(), any()) }
    }

    @Test
    fun `returns early when backoff is active`() = runTest {
        worker.nextRetryAt = Instant.now().plusSeconds(60)
        worker.pollConfig()
        coVerify(exactly = 0) { cloudApiClient.getConfig(any(), any()) }
    }

    @Test
    fun `returns early when no access token available`() = runTest {
        every { tokenProvider.getAccessToken() } returns null
        worker.pollConfig()
        coVerify(exactly = 0) { cloudApiClient.getConfig(any(), any()) }
    }

    // -------------------------------------------------------------------------
    // 304 Not Modified
    // -------------------------------------------------------------------------

    @Test
    fun `304 not modified does not apply config`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.NotModified

        worker.pollConfig()

        coVerify(exactly = 0) { configManager.applyConfig(any(), any()) }
        assertEquals(0, worker.consecutiveFailureCount)
    }

    @Test
    fun `304 not modified updates lastConfigPullAt`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.NotModified

        worker.pollConfig()

        val syncSlot = slot<SyncState>()
        coVerify { syncStateDao.upsert(capture(syncSlot)) }
        assertTrue(syncSlot.captured.lastConfigPullAt != null)
    }

    // -------------------------------------------------------------------------
    // 200 OK — successful config apply
    // -------------------------------------------------------------------------

    @Test
    fun `200 OK parses and applies config via ConfigManager`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.Success(VALID_CONFIG_JSON, "\"5\"")
        coEvery { configManager.applyConfig(any(), any()) } returns ConfigApplyResult.Applied

        worker.pollConfig()

        coVerify { configManager.applyConfig(any<EdgeAgentConfigDto>(), eq(VALID_CONFIG_JSON)) }
    }

    @Test
    fun `applied config updates SyncState with configVersion`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.Success(VALID_CONFIG_JSON, "\"5\"")
        coEvery { configManager.applyConfig(any(), any()) } returns ConfigApplyResult.Applied

        worker.pollConfig()

        val syncSlot = slot<SyncState>()
        coVerify { syncStateDao.upsert(capture(syncSlot)) }
        assertEquals(5, syncSlot.captured.lastConfigVersion)
        assertTrue(syncSlot.captured.lastConfigPullAt != null)
    }

    @Test
    fun `applied config resets backoff`() = runTest {
        worker.consecutiveFailureCount = 3
        worker.nextRetryAt = Instant.now().minusSeconds(1) // past backoff

        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.Success(VALID_CONFIG_JSON, "\"5\"")
        coEvery { configManager.applyConfig(any(), any()) } returns ConfigApplyResult.Applied

        worker.pollConfig()

        assertEquals(0, worker.consecutiveFailureCount)
        assertEquals(Instant.EPOCH, worker.nextRetryAt)
    }

    // -------------------------------------------------------------------------
    // ConfigManager rejection
    // -------------------------------------------------------------------------

    @Test
    fun `rejected config does not update configVersion in SyncState`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.Success(VALID_CONFIG_JSON, "\"5\"")
        coEvery { configManager.applyConfig(any(), any()) } returns
            ConfigApplyResult.Rejected("REPROVISION_REQUIRED")

        worker.pollConfig()

        val syncSlot = slot<SyncState>()
        coVerify { syncStateDao.upsert(capture(syncSlot)) }
        // lastConfigVersion should NOT be updated for rejected configs
        assertEquals(null, syncSlot.captured.lastConfigVersion)
    }

    // -------------------------------------------------------------------------
    // 401 → token refresh
    // -------------------------------------------------------------------------

    @Test
    fun `401 triggers refresh and retries successfully`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns true
        every { tokenProvider.getAccessToken() } returns "refreshed-token"
        coEvery { cloudApiClient.getConfig(4, "refreshed-token") } returns
            CloudConfigPollResult.Success(VALID_CONFIG_JSON, "\"5\"")
        coEvery { configManager.applyConfig(any(), any()) } returns ConfigApplyResult.Applied

        worker.pollConfig()

        coVerify { configManager.applyConfig(any(), any()) }
        assertEquals(0, worker.consecutiveFailureCount)
    }

    @Test
    fun `401 with failed refresh increments failure count`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns false

        worker.pollConfig()

        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.now().minusSeconds(1)))
    }

    // -------------------------------------------------------------------------
    // 403 DEVICE_DECOMMISSIONED
    // -------------------------------------------------------------------------

    @Test
    fun `403 DEVICE_DECOMMISSIONED calls markDecommissioned`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.Forbidden("DEVICE_DECOMMISSIONED")
        every { tokenProvider.markDecommissioned() } returns Unit

        worker.pollConfig()

        verify { tokenProvider.markDecommissioned() }
    }

    @Test
    fun `403 non-decommission error increments failure count`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.Forbidden("ACCESS_DENIED")

        worker.pollConfig()

        assertEquals(1, worker.consecutiveFailureCount)
    }

    // -------------------------------------------------------------------------
    // Transport error and backoff
    // -------------------------------------------------------------------------

    @Test
    fun `transport error increments failure count and sets backoff`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.TransportError("Connection timeout")

        worker.pollConfig()

        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.now().minusSeconds(1)))
    }

    @Test
    fun `consecutive failures increase backoff exponentially`() = runTest {
        val backoff1 = worker.calculateBackoffMs(1)
        val backoff2 = worker.calculateBackoffMs(2)
        val backoff3 = worker.calculateBackoffMs(3)

        assertEquals(1_000L, backoff1)
        assertEquals(2_000L, backoff2)
        assertEquals(4_000L, backoff3)
    }

    @Test
    fun `backoff is capped at 60 seconds`() = runTest {
        val backoff = worker.calculateBackoffMs(10)
        assertEquals(60_000L, backoff)
    }

    // -------------------------------------------------------------------------
    // Malformed JSON
    // -------------------------------------------------------------------------

    @Test
    fun `malformed JSON response increments failure count`() = runTest {
        coEvery { cloudApiClient.getConfig(4, VALID_TOKEN) } returns
            CloudConfigPollResult.Success("{ not valid json", null)

        worker.pollConfig()

        assertEquals(1, worker.consecutiveFailureCount)
        coVerify(exactly = 0) { configManager.applyConfig(any(), any()) }
    }

    // -------------------------------------------------------------------------
    // First poll (no current version)
    // -------------------------------------------------------------------------

    @Test
    fun `first poll sends null currentConfigVersion`() = runTest {
        every { configManager.currentConfigVersion } returns null
        coEvery { cloudApiClient.getConfig(null, VALID_TOKEN) } returns
            CloudConfigPollResult.Success(VALID_CONFIG_JSON, "\"5\"")
        coEvery { configManager.applyConfig(any(), any()) } returns ConfigApplyResult.Applied

        worker.pollConfig()

        coVerify { cloudApiClient.getConfig(null, VALID_TOKEN) }
    }
}
