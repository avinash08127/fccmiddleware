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
 * StatusPollWorkerTest — unit tests for EA-3.2 SYNCED_TO_ODOO Status Poller.
 *
 * Validates:
 *   - No-ops when dependencies are null
 *   - No-ops when device is decommissioned
 *   - No-ops when backoff is active
 *   - No-ops when no UPLOADED records exist
 *   - No-ops when access token is unavailable
 *   - SYNCED_TO_ODOO entries → markSyncedToOdoo() called
 *   - Non-SYNCED_TO_ODOO entries → not marked
 *   - Mixed statuses → only SYNCED_TO_ODOO are marked
 *   - lastStatusPollAt updated after successful poll
 *   - SyncState created when none exists
 *   - 401 → token refresh + retry → success
 *   - 401 → refresh fails → transport failure with backoff
 *   - 403 DEVICE_DECOMMISSIONED → markDecommissioned()
 *   - 403 non-decommission → failure with backoff
 *   - Transport error → failure count increases, backoff applied
 *   - Successful poll after failures resets backoff
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

    // -------------------------------------------------------------------------
    // No-op guards
    // -------------------------------------------------------------------------

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
    fun `returns early when cloudApiClient is null`() = runTest {
        val w = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = null,
            tokenProvider = tokenProvider,
        )
        w.pollSyncedToOdooStatus()
    }

    @Test
    fun `returns early when tokenProvider is null`() = runTest {
        val w = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = null,
        )
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        w.pollSyncedToOdooStatus()
        coVerify(exactly = 0) { cloudApiClient.getSyncedStatus(any(), any()) }
    }

    @Test
    fun `returns early when device is decommissioned`() = runTest {
        every { tokenProvider.isDecommissioned() } returns true
        worker.pollSyncedToOdooStatus()
        coVerify(exactly = 0) { bufferManager.getUploadedFccTransactionIds(any()) }
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
    fun `returns early when no UPLOADED records`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns emptyList()
        worker.pollSyncedToOdooStatus()
        coVerify(exactly = 0) { cloudApiClient.getSyncedStatus(any(), any()) }
    }

    @Test
    fun `returns early when access token is null`() = runTest {
        every { tokenProvider.getAccessToken() } returns null
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("FCC-1")
        worker.pollSyncedToOdooStatus()
        coVerify(exactly = 0) { cloudApiClient.getSyncedStatus(any(), any()) }
    }

    // -------------------------------------------------------------------------
    // Successful poll — SYNCED_TO_ODOO transitions
    // -------------------------------------------------------------------------

    @Test
    fun `SYNCED_TO_ODOO entries are marked locally`() = runTest {
        val ids = listOf("FCC-1", "FCC-2", "FCC-3")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(
                statuses = listOf(
                    TransactionStatusEntry("FCC-1", "SYNCED_TO_ODOO"),
                    TransactionStatusEntry("FCC-2", "SYNCED_TO_ODOO"),
                    TransactionStatusEntry("FCC-3", "PENDING"),
                ),
            ),
        )

        worker.pollSyncedToOdooStatus()

        coVerify { bufferManager.markSyncedToOdoo(listOf("FCC-1", "FCC-2")) }
    }

    @Test
    fun `non-SYNCED_TO_ODOO entries are not marked`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(
                statuses = listOf(
                    TransactionStatusEntry("FCC-1", "PENDING"),
                ),
            ),
        )

        worker.pollSyncedToOdooStatus()

        coVerify(exactly = 0) { bufferManager.markSyncedToOdoo(any()) }
    }

    @Test
    fun `empty response does not call markSyncedToOdoo`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(statuses = emptyList()),
        )

        worker.pollSyncedToOdooStatus()

        coVerify(exactly = 0) { bufferManager.markSyncedToOdoo(any()) }
    }

    @Test
    fun `successful poll updates SyncState_lastStatusPollAt`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(
                statuses = listOf(TransactionStatusEntry("FCC-1", "SYNCED_TO_ODOO")),
            ),
        )
        val stateSlot = slot<SyncState>()
        coEvery { syncStateDao.upsert(capture(stateSlot)) } returns Unit

        worker.pollSyncedToOdooStatus()

        coVerify { syncStateDao.upsert(any()) }
        assertNotNull(stateSlot.captured.lastStatusPollAt)
    }

    @Test
    fun `successful poll creates SyncState when none exists`() = runTest {
        coEvery { syncStateDao.get() } returns null
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(statuses = emptyList()),
        )

        worker.pollSyncedToOdooStatus()

        val stateSlot = slot<SyncState>()
        coVerify { syncStateDao.upsert(capture(stateSlot)) }
        assertNotNull(stateSlot.captured.lastStatusPollAt)
        assertEquals(1, stateSlot.captured.id)
    }

    @Test
    fun `successful poll resets failure count and backoff`() = runTest {
        worker.statusPollConsecutiveFailureCount = 3
        worker.statusPollNextRetryAt = Instant.EPOCH // expired backoff

        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(statuses = emptyList()),
        )

        worker.pollSyncedToOdooStatus()

        assertEquals(0, worker.statusPollConsecutiveFailureCount)
        assertEquals(Instant.EPOCH, worker.statusPollNextRetryAt)
    }

    // -------------------------------------------------------------------------
    // 401 — token refresh
    // -------------------------------------------------------------------------

    @Test
    fun `401 triggers token refresh and retry, marks SYNCED_TO_ODOO on success`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { tokenProvider.refreshAccessToken() } returns true
        every { tokenProvider.getAccessToken() } returnsMany listOf("old-token", "new-token")

        coEvery { cloudApiClient.getSyncedStatus(ids, "old-token") } returns CloudStatusPollResult.Unauthorized
        coEvery { cloudApiClient.getSyncedStatus(ids, "new-token") } returns CloudStatusPollResult.Success(
            SyncedStatusResponse(
                statuses = listOf(TransactionStatusEntry("FCC-1", "SYNCED_TO_ODOO")),
            ),
        )

        worker.pollSyncedToOdooStatus()

        coVerify { tokenProvider.refreshAccessToken() }
        coVerify { bufferManager.markSyncedToOdoo(listOf("FCC-1")) }
        assertEquals(0, worker.statusPollConsecutiveFailureCount)
    }

    @Test
    fun `401 records failure when refreshAccessToken returns false`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns CloudStatusPollResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns false

        worker.pollSyncedToOdooStatus()

        coVerify { tokenProvider.refreshAccessToken() }
        coVerify(exactly = 0) { bufferManager.markSyncedToOdoo(any()) }
        assertEquals(1, worker.statusPollConsecutiveFailureCount)
        assertTrue(worker.statusPollNextRetryAt.isAfter(Instant.EPOCH))
    }

    // -------------------------------------------------------------------------
    // 403 — decommission
    // -------------------------------------------------------------------------

    @Test
    fun `403 DEVICE_DECOMMISSIONED calls markDecommissioned`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns
            CloudStatusPollResult.Forbidden("DEVICE_DECOMMISSIONED")

        worker.pollSyncedToOdooStatus()

        coVerify { tokenProvider.markDecommissioned() }
        coVerify(exactly = 0) { bufferManager.markSyncedToOdoo(any()) }
        assertEquals(0, worker.statusPollConsecutiveFailureCount)
    }

    @Test
    fun `403 non-decommission records failure with backoff`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns
            CloudStatusPollResult.Forbidden("ACCESS_DENIED")

        worker.pollSyncedToOdooStatus()

        coVerify(exactly = 0) { tokenProvider.markDecommissioned() }
        assertEquals(1, worker.statusPollConsecutiveFailureCount)
        assertTrue(worker.statusPollNextRetryAt.isAfter(Instant.EPOCH))
    }

    // -------------------------------------------------------------------------
    // Transport errors and backoff
    // -------------------------------------------------------------------------

    @Test
    fun `transport error increments failure count and sets backoff`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns
            CloudStatusPollResult.TransportError("Connection timed out")

        worker.pollSyncedToOdooStatus()

        assertEquals(1, worker.statusPollConsecutiveFailureCount)
        assertTrue(worker.statusPollNextRetryAt.isAfter(Instant.EPOCH))
    }

    @Test
    fun `multiple consecutive failures increase failure count`() = runTest {
        val ids = listOf("FCC-1")
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns ids
        coEvery { cloudApiClient.getSyncedStatus(ids, any()) } returns
            CloudStatusPollResult.TransportError("Network error")

        worker.pollSyncedToOdooStatus()
        worker.statusPollNextRetryAt = Instant.EPOCH // expire backoff for next attempt
        worker.pollSyncedToOdooStatus()

        assertEquals(2, worker.statusPollConsecutiveFailureCount)
    }
}
