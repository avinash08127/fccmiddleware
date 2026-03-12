package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.config.ConfigApplyResult
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.EdgeAgentConfigDto
import com.fccmiddleware.edge.config.canonicalEdgeConfigJson
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
        private val VALID_CONFIG_JSON = canonicalEdgeConfigJson(configVersion = 5)
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
        worker.circuitBreaker.nextRetryAt = Instant.now().plusSeconds(60)
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
        worker.circuitBreaker.consecutiveFailureCount = 3
        worker.circuitBreaker.nextRetryAt = Instant.now().minusSeconds(1) // past backoff

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

        // Rejected config records a failure — no SyncState upsert should occur
        coVerify(exactly = 0) { syncStateDao.upsert(any()) }
        assertTrue(worker.consecutiveFailureCount > 0)
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
        // Backoff is now in CircuitBreaker — test via recordFailure() return value
        val cb = worker.circuitBreaker
        cb.resetOnConnectivityRecovery() // ensure clean state
        assertEquals(1_000L, cb.recordFailure())
        assertEquals(2_000L, cb.recordFailure())
        assertEquals(4_000L, cb.recordFailure())
    }

    @Test
    fun `backoff is capped at 60 seconds`() = runTest {
        val cb = worker.circuitBreaker
        cb.resetOnConnectivityRecovery()
        repeat(9) { cb.recordFailure() }
        assertEquals(60_000L, cb.recordFailure()) // 10th failure
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
