package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.SyncState
import io.mockk.Runs
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.just
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant

/**
 * StatusPollWorkerTest — unit tests for the timestamp-based SYNCED_TO_ODOO status poller.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class StatusPollWorkerTest {

    private val bufferManager: TransactionBufferManager = mockk(relaxed = true)
    private val syncStateDao: SyncStateDao = mockk(relaxed = true)
    private val cloudApiClient: CloudApiClient = mockk()
    private val tokenProvider: DeviceTokenProvider = mockk()

    private lateinit var worker: CloudUploadWorker

    @Before
    fun setUp() {
        worker = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )
        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.markDecommissioned() } just Runs
        every { tokenProvider.getAccessToken() } returns "valid-jwt-token"
        every { tokenProvider.getLegalEntityId() } returns "10000000-0000-0000-0000-000000000001"
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit
    }

    @Test
    fun `returns early when bufferManager is null`() = runTest {
        val w = CloudUploadWorker(
            bufferManager = null,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )
        w.pollSyncedToOdooStatus()
        coVerify(exactly = 0) { cloudApiClient.getSyncedStatus(any(), any()) }
    }

    @Test
    fun `returns early when device is decommissioned`() = runTest {
        every { tokenProvider.isDecommissioned() } returns true
        worker.pollSyncedToOdooStatus()
        coVerify(exactly = 0) { cloudApiClient.getSyncedStatus(any(), any()) }
    }

    @Test
    fun `returns early when backoff is active`() = runTest {
        worker.statusPollCircuitBreaker.nextRetryAt = Instant.now().plusSeconds(60)
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        worker.pollSyncedToOdooStatus()
        coVerify(exactly = 0) { cloudApiClient.getSyncedStatus(any(), any()) }
    }

    @Test
    fun `returns early when no uploaded records exist`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns emptyList()
        worker.pollSyncedToOdooStatus()
        coVerify(exactly = 0) { cloudApiClient.getSyncedStatus(any(), any()) }
    }

    @Test
    fun `first poll uses epoch watermark`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery {
            cloudApiClient.getSyncedStatus("1970-01-01T00:00:00Z", any())
        } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(fccTransactionIds = listOf("FCC-1")),
        )

        worker.pollSyncedToOdooStatus()

        coVerify { cloudApiClient.getSyncedStatus("1970-01-01T00:00:00Z", any()) }
        coVerify { bufferManager.markSyncedToOdoo(listOf("FCC-1")) }
    }

    @Test
    fun `successful poll marks only returned ids that are still uploaded locally`() = runTest {
        val uploadedIds = listOf("FCC-1", "FCC-2")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns uploadedIds
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(
                fccTransactionIds = listOf("FCC-1", "FCC-3", "FCC-1"),
            ),
        )

        worker.pollSyncedToOdooStatus()

        coVerify { bufferManager.markSyncedToOdoo(listOf("FCC-1")) }
        coVerify(exactly = 0) { bufferManager.revertToPending(any()) }
    }

    @Test
    fun `empty response does not call markSyncedToOdoo`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(fccTransactionIds = emptyList()),
        )

        worker.pollSyncedToOdooStatus()

        coVerify(exactly = 0) { bufferManager.markSyncedToOdoo(any()) }
    }

    @Test
    fun `existing watermark is reused on next poll`() = runTest {
        val since = "2026-03-11T10:00:00Z"
        coEvery { syncStateDao.get() } returns SyncState(
            lastFccCursor = null,
            lastUploadAt = null,
            lastStatusPollAt = since,
            lastConfigPullAt = null,
            lastConfigVersion = null,
            updatedAt = since,
        )
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        coEvery { cloudApiClient.getSyncedStatus(since, any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(fccTransactionIds = emptyList()),
        )

        worker.pollSyncedToOdooStatus()

        coVerify { cloudApiClient.getSyncedStatus(since, any()) }
    }

    @Test
    fun `successful poll updates SyncState lastStatusPollAt`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(fccTransactionIds = listOf("FCC-1")),
        )
        val stateSlot = slot<SyncState>()
        coEvery { syncStateDao.upsert(capture(stateSlot)) } returns Unit

        worker.pollSyncedToOdooStatus()

        assertNotNull(stateSlot.captured.lastStatusPollAt)
        assertEquals(stateSlot.captured.lastStatusPollAt, stateSlot.captured.updatedAt)
    }

    @Test
    fun `successful poll resets failure count and backoff`() = runTest {
        worker.statusPollCircuitBreaker.consecutiveFailureCount = 3
        worker.statusPollCircuitBreaker.nextRetryAt = Instant.EPOCH
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(fccTransactionIds = emptyList()),
        )

        worker.pollSyncedToOdooStatus()

        assertEquals(0, worker.statusPollConsecutiveFailureCount)
        assertEquals(Instant.EPOCH, worker.statusPollNextRetryAt)
    }

    @Test
    fun `401 triggers token refresh and retry`() = runTest {
        val since = "2026-03-11T10:00:00Z"
        coEvery { syncStateDao.get() } returns SyncState(
            lastFccCursor = null,
            lastUploadAt = null,
            lastStatusPollAt = since,
            lastConfigPullAt = null,
            lastConfigVersion = null,
            updatedAt = since,
        )
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        coEvery { tokenProvider.refreshAccessToken() } returns true
        every { tokenProvider.getAccessToken() } returnsMany listOf("old-token", "new-token")
        coEvery { cloudApiClient.getSyncedStatus(since, "old-token") } returns CloudStatusPollResult.Unauthorized
        coEvery { cloudApiClient.getSyncedStatus(since, "new-token") } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(fccTransactionIds = listOf("FCC-1")),
        )

        worker.pollSyncedToOdooStatus()

        coVerify { tokenProvider.refreshAccessToken() }
        coVerify { bufferManager.markSyncedToOdoo(listOf("FCC-1")) }
    }

    @Test
    fun `401 records failure when refreshAccessToken returns false`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns CloudStatusPollResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns false

        worker.pollSyncedToOdooStatus()

        assertEquals(1, worker.statusPollConsecutiveFailureCount)
        assertTrue(worker.statusPollNextRetryAt.isAfter(Instant.EPOCH))
    }

    @Test
    fun `403 device decommissioned calls markDecommissioned`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns
            CloudStatusPollResult.Forbidden("DEVICE_DECOMMISSIONED")

        worker.pollSyncedToOdooStatus()

        coVerify { tokenProvider.markDecommissioned() }
    }

    @Test
    fun `transport error increments failure count and sets backoff`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns
            CloudStatusPollResult.TransportError("Connection timed out")

        worker.pollSyncedToOdooStatus()

        assertEquals(1, worker.statusPollConsecutiveFailureCount)
        assertTrue(worker.statusPollNextRetryAt.isAfter(Instant.EPOCH))
    }
}
