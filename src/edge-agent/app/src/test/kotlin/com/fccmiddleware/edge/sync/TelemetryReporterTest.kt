package com.fccmiddleware.edge.sync

import android.content.Context
import android.os.BatteryManager
import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.StatusCount
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.EdgeAgentConfigDto
import com.fccmiddleware.edge.config.canonicalEdgeConfig
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant

/**
 * TelemetryReporterTest — unit tests for EA-3.4 Telemetry Reporter.
 *
 * Validates:
 *   - buildPayload returns null when config is not loaded
 *   - buildPayload populates all required fields from config, DAO, and connectivity
 *   - Buffer status counts are correctly mapped from DAO
 *   - Sync status fields populated from SyncState entity
 *   - Sync lag computed from oldest PENDING record
 *   - Error counters are captured in snapshot
 *   - resetErrorCounts sets all counters to 0
 *   - Sequence number increments and persists to SyncState
 *   - Sequence number starts at 1 when no SyncState exists
 *   - reportTelemetry in CloudUploadWorker: no-ops when reporter is null
 *   - reportTelemetry: no-ops when cloudApiClient or tokenProvider is null
 *   - reportTelemetry: no-ops when device is decommissioned
 *   - reportTelemetry: submits payload on success and resets error counts
 *   - reportTelemetry: handles 401 with token refresh + retry
 *   - reportTelemetry: handles transport error (fire-and-forget, no crash)
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class TelemetryReporterTest {

    private val context: Context = mockk(relaxed = true)
    private val transactionDao: TransactionBufferDao = mockk(relaxed = true)
    private val syncStateDao: SyncStateDao = mockk(relaxed = true)
    private val connectivityManager: ConnectivityManager = mockk(relaxed = true)
    private val configManager: ConfigManager = mockk(relaxed = true)
    private val cloudApiClient: CloudApiClient = mockk()
    private val tokenProvider: DeviceTokenProvider = mockk()

    private lateinit var reporter: TelemetryReporter

    private val connectivityStateFlow = MutableStateFlow(ConnectivityState.FULLY_ONLINE)

    private val baseConfig = canonicalEdgeConfig(configVersion = 1)

    private val testConfig: EdgeAgentConfigDto = baseConfig.copy(
        identity = baseConfig.identity.copy(
            deviceId = "device-001",
            siteCode = "SITE-A",
            legalEntityId = "10000000-0000-0000-0000-000000000001",
        ),
        site = baseConfig.site.copy(connectivityMode = "RELAY"),
        fcc = baseConfig.fcc.copy(
            credentialRef = "secret/fcc-key",
        ),
    )

    @Before
    fun setUp() {
        every { connectivityManager.state } returns connectivityStateFlow
        every { connectivityManager.fccHeartbeatAgeSeconds() } returns 15
        every { connectivityManager.lastFccSuccessMs } returns System.currentTimeMillis() - 15_000L

        every { configManager.config } returns MutableStateFlow(testConfig)

        // AP-021: Battery manager mock (replaces sticky broadcast receiver IPC approach)
        val batteryManager = mockk<BatteryManager>(relaxed = true)
        every { batteryManager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY) } returns 85
        every { batteryManager.isCharging } returns true
        every { context.getSystemService(Context.BATTERY_SERVICE) } returns batteryManager

        // Database path mock
        val dbFile = mockk<java.io.File>(relaxed = true)
        every { dbFile.absolutePath } returns "/data/data/com.fccmiddleware.edge/databases/edge_buffer.db"
        every { context.getDatabasePath(any()) } returns dbFile

        // DAO mocks
        coEvery { transactionDao.countByStatus() } returns listOf(
            StatusCount("PENDING", 10),
            StatusCount("UPLOADED", 25),
            StatusCount("SYNCED_TO_ODOO", 100),
            StatusCount("FAILED", 2),
        )
        coEvery { transactionDao.oldestPendingCreatedAt() } returns
            Instant.now().minusSeconds(120).toString()

        coEvery { syncStateDao.get() } returns SyncState(
            lastFccCursor = "cursor-1",
            lastUploadAt = "2026-03-11T10:00:00Z",
            lastUploadAttemptAt = "2026-03-11T10:02:00Z",
            lastStatusPollAt = "2026-03-11T10:01:00Z",
            lastConfigPullAt = "2026-03-11T09:00:00Z",
            lastConfigVersion = 5,
            telemetrySequence = 42L,
            updatedAt = "2026-03-11T10:01:00Z",
        )
        coEvery { syncStateDao.upsert(any()) } returns Unit
        coEvery { syncStateDao.incrementAndGetTelemetrySequence(any()) } returns 43L

        // Token provider defaults
        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns "valid-jwt"
        every { tokenProvider.getLegalEntityId() } returns "10000000-0000-0000-0000-000000000001"

        reporter = TelemetryReporter(
            context = context,
            transactionDao = transactionDao,
            syncStateDao = syncStateDao,
            connectivityManager = connectivityManager,
            configManager = configManager,
            appVersion = "1.0.0",
            serviceStartTimeMs = 0L,
            databasePath = null,
        )
    }

    // -------------------------------------------------------------------------
    // buildPayload tests
    // -------------------------------------------------------------------------

    @Test
    fun `buildPayload returns null when config not loaded`() = runTest {
        every { configManager.config } returns MutableStateFlow(null)
        val reporter = TelemetryReporter(
            context = context,
            transactionDao = transactionDao,
            syncStateDao = syncStateDao,
            connectivityManager = connectivityManager,
            configManager = configManager,
        )
        assertNull(reporter.buildPayload())
    }

    @Test
    fun `buildPayload populates identity fields from config`() = runTest {
        val payload = reporter.buildPayload()
        assertNotNull(payload)
        assertEquals("device-001", payload!!.deviceId)
        assertEquals("SITE-A", payload.siteCode)
        assertEquals("10000000-0000-0000-0000-000000000001", payload.legalEntityId)
        assertEquals("1.0", payload.schemaVersion)
    }

    @Test
    fun `buildPayload captures connectivity state`() = runTest {
        connectivityStateFlow.value = ConnectivityState.INTERNET_DOWN
        val payload = reporter.buildPayload()
        assertEquals("INTERNET_DOWN", payload!!.connectivityState)
    }

    @Test
    fun `buildPayload populates device status with battery and storage`() = runTest {
        val payload = reporter.buildPayload()!!
        assertEquals(85, payload.device.batteryPercent)
        assertTrue(payload.device.isCharging)
        assertEquals("1.0.0", payload.device.appVersion)
    }

    @Test
    fun `buildPayload populates FCC health from connectivity manager and config`() = runTest {
        val payload = reporter.buildPayload()!!
        assertTrue(payload.fccHealth.isReachable)
        assertEquals(15, payload.fccHealth.heartbeatAgeSeconds)
        assertNotNull(payload.fccHealth.lastHeartbeatAtUtc)
        assertEquals("DOMS", payload.fccHealth.fccVendor)
        assertEquals("192.168.1.100", payload.fccHealth.fccHost)
        assertEquals(8080, payload.fccHealth.fccPort)
    }

    @Test
    fun `buildPayload populates FCC health isReachable false when FCC unreachable`() = runTest {
        connectivityStateFlow.value = ConnectivityState.FCC_UNREACHABLE
        val payload = reporter.buildPayload()!!
        assertEquals(false, payload.fccHealth.isReachable)
    }

    @Test
    fun `buildPayload populates buffer status from DAO counts`() = runTest {
        val payload = reporter.buildPayload()!!
        assertEquals(137, payload.buffer.totalRecords) // 10+25+100+2
        assertEquals(10, payload.buffer.pendingUploadCount)
        assertEquals(25, payload.buffer.syncedCount)
        assertEquals(100, payload.buffer.syncedToOdooCount)
        assertEquals(2, payload.buffer.failedCount)
        assertNotNull(payload.buffer.oldestPendingAtUtc)
    }

    @Test
    fun `buildPayload populates sync status from SyncState entity`() = runTest {
        val payload = reporter.buildPayload()!!
        // AF-035: lastSyncAttemptUtc now maps to lastUploadAttemptAt (attempt timestamp)
        assertEquals("2026-03-11T10:02:00Z", payload.sync.lastSyncAttemptUtc)
        assertEquals("2026-03-11T10:00:00Z", payload.sync.lastSuccessfulSyncUtc)
        assertEquals("2026-03-11T10:01:00Z", payload.sync.lastStatusPollUtc)
        assertEquals("2026-03-11T09:00:00Z", payload.sync.lastConfigPullUtc)
        assertEquals("5", payload.sync.configVersion)
        assertEquals(50, payload.sync.uploadBatchSize)
    }

    @Test
    fun `buildPayload computes sync lag from oldest PENDING record`() = runTest {
        val payload = reporter.buildPayload()!!
        // Oldest pending is ~120 seconds ago
        assertNotNull(payload.sync.syncLagSeconds)
        assertTrue(payload.sync.syncLagSeconds!! >= 118) // Allow 2s tolerance
        assertTrue(payload.sync.syncLagSeconds!! <= 125)
    }

    @Test
    fun `buildPayload sync lag is null when no pending records`() = runTest {
        coEvery { transactionDao.oldestPendingCreatedAt() } returns null
        val payload = reporter.buildPayload()!!
        assertNull(payload.sync.syncLagSeconds)
    }

    @Test
    fun `buildPayload captures error count snapshot`() = runTest {
        reporter.fccConnectionErrors.set(3)
        reporter.cloudUploadErrors.set(7)
        reporter.cloudAuthErrors.set(1)
        reporter.localApiErrors.set(2)
        reporter.bufferWriteErrors.set(0)
        reporter.adapterNormalizationErrors.set(4)
        reporter.preAuthErrors.set(1)

        val payload = reporter.buildPayload()!!
        assertEquals(3, payload.errorCounts.fccConnectionErrors)
        assertEquals(7, payload.errorCounts.cloudUploadErrors)
        assertEquals(1, payload.errorCounts.cloudAuthErrors)
        assertEquals(2, payload.errorCounts.localApiErrors)
        assertEquals(0, payload.errorCounts.bufferWriteErrors)
        assertEquals(4, payload.errorCounts.adapterNormalizationErrors)
        assertEquals(1, payload.errorCounts.preAuthErrors)
    }

    @Test
    fun `buildPayload increments sequence number from SyncState`() = runTest {
        val payload = reporter.buildPayload()!!
        assertEquals(43L, payload.sequenceNumber) // 42 + 1

        coVerify { syncStateDao.incrementAndGetTelemetrySequence(any()) }
    }

    @Test
    fun `buildPayload sequence starts at 1 when no SyncState exists`() = runTest {
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.incrementAndGetTelemetrySequence(any()) } returns 1L
        val payload = reporter.buildPayload()!!
        assertEquals(1L, payload.sequenceNumber)
    }

    // -------------------------------------------------------------------------
    // resetErrorCounts
    // -------------------------------------------------------------------------

    @Test
    fun `resetErrorCounts sets all counters to zero`() {
        reporter.fccConnectionErrors.set(5)
        reporter.cloudUploadErrors.set(3)
        reporter.cloudAuthErrors.set(2)
        reporter.localApiErrors.set(1)
        reporter.bufferWriteErrors.set(4)
        reporter.adapterNormalizationErrors.set(6)
        reporter.preAuthErrors.set(7)

        reporter.resetErrorCounts()

        assertEquals(0, reporter.fccConnectionErrors.get())
        assertEquals(0, reporter.cloudUploadErrors.get())
        assertEquals(0, reporter.cloudAuthErrors.get())
        assertEquals(0, reporter.localApiErrors.get())
        assertEquals(0, reporter.bufferWriteErrors.get())
        assertEquals(0, reporter.adapterNormalizationErrors.get())
        assertEquals(0, reporter.preAuthErrors.get())
    }

    // -------------------------------------------------------------------------
    // CloudUploadWorker.reportTelemetry() integration
    // -------------------------------------------------------------------------

    @Test
    fun `reportTelemetry no-ops when telemetryReporter is null`() = runTest {
        val worker = CloudUploadWorker(
            bufferManager = mockk(relaxed = true),
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            telemetryReporter = null,
        )
        worker.reportTelemetry() // Should not throw
    }

    @Test
    fun `reportTelemetry no-ops when cloudApiClient is null`() = runTest {
        val worker = CloudUploadWorker(
            bufferManager = mockk(relaxed = true),
            syncStateDao = syncStateDao,
            cloudApiClient = null,
            tokenProvider = tokenProvider,
            telemetryReporter = reporter,
        )
        worker.reportTelemetry() // Should not throw
    }

    @Test
    fun `reportTelemetry no-ops when tokenProvider is null`() = runTest {
        val worker = CloudUploadWorker(
            bufferManager = mockk(relaxed = true),
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = null,
            telemetryReporter = reporter,
        )
        worker.reportTelemetry() // Should not throw
    }

    @Test
    fun `reportTelemetry no-ops when device is decommissioned`() = runTest {
        every { tokenProvider.isDecommissioned() } returns true
        val worker = CloudUploadWorker(
            bufferManager = mockk(relaxed = true),
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            telemetryReporter = reporter,
        )
        worker.reportTelemetry()
        coVerify(exactly = 0) { cloudApiClient.submitTelemetry(any(), any()) }
    }

    @Test
    fun `reportTelemetry submits payload and resets error counts on success`() = runTest {
        reporter.cloudUploadErrors.set(5)
        reporter.fccConnectionErrors.set(3)

        coEvery { cloudApiClient.submitTelemetry(any(), any()) } returns CloudTelemetryResult.Success

        val worker = CloudUploadWorker(
            bufferManager = mockk(relaxed = true),
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            telemetryReporter = reporter,
        )
        worker.reportTelemetry()

        coVerify(exactly = 1) { cloudApiClient.submitTelemetry(any(), "valid-jwt") }
        assertEquals(0, reporter.cloudUploadErrors.get())
        assertEquals(0, reporter.fccConnectionErrors.get())
    }

    @Test
    fun `reportTelemetry handles 401 with token refresh and retries`() = runTest {
        coEvery { cloudApiClient.submitTelemetry(any(), "valid-jwt") } returns CloudTelemetryResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns true
        every { tokenProvider.getAccessToken() } returnsMany listOf("valid-jwt", "fresh-jwt")
        coEvery { cloudApiClient.submitTelemetry(any(), "fresh-jwt") } returns CloudTelemetryResult.Success

        val worker = CloudUploadWorker(
            bufferManager = mockk(relaxed = true),
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            telemetryReporter = reporter,
        )
        worker.reportTelemetry()

        coVerify(exactly = 1) { tokenProvider.refreshAccessToken() }
        coVerify(exactly = 1) { cloudApiClient.submitTelemetry(any(), "fresh-jwt") }
    }

    @Test
    fun `reportTelemetry handles transport error without crashing`() = runTest {
        coEvery { cloudApiClient.submitTelemetry(any(), any()) } returns
            CloudTelemetryResult.TransportError("Network timeout")

        val worker = CloudUploadWorker(
            bufferManager = mockk(relaxed = true),
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            telemetryReporter = reporter,
        )
        worker.reportTelemetry() // fire-and-forget: should not throw
    }

    @Test
    fun `reportTelemetry skips when config not loaded (payload null)`() = runTest {
        every { configManager.config } returns MutableStateFlow(null)
        val reporterNoConfig = TelemetryReporter(
            context = context,
            transactionDao = transactionDao,
            syncStateDao = syncStateDao,
            connectivityManager = connectivityManager,
            configManager = configManager,
        )
        val worker = CloudUploadWorker(
            bufferManager = mockk(relaxed = true),
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            telemetryReporter = reporterNoConfig,
        )
        worker.reportTelemetry()
        coVerify(exactly = 0) { cloudApiClient.submitTelemetry(any(), any()) }
    }
}
