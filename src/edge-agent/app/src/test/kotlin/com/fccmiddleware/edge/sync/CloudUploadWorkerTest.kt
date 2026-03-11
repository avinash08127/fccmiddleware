package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
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
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant
import java.util.UUID

/**
 * CloudUploadWorkerTest — unit tests for EA-3.1 Cloud Upload Worker.
 *
 * Validates:
 *   - No-ops when any required dependency is null
 *   - No-ops when device is decommissioned
 *   - No-ops when backoff period is active
 *   - No-ops when no PENDING records exist
 *   - No-ops when access token is unavailable
 *   - ACCEPTED outcome → records marked UPLOADED, failure count reset
 *   - DUPLICATE outcome → records marked UPLOADED (same as ACCEPTED per §5.3)
 *   - Mixed ACCEPTED + DUPLICATE in one batch → both marked UPLOADED
 *   - REJECTED outcome → recordUploadFailure() called, stays PENDING
 *   - 401 → refreshAccessToken() called, retry succeeds → UPLOADED
 *   - 401 → refreshAccessToken() returns false → failure recorded with backoff
 *   - 401 → refresh succeeds but retry still 401 → failure recorded
 *   - 403 DEVICE_DECOMMISSIONED → markDecommissioned() called, no backoff
 *   - 403 non-decommission → failure recorded with backoff
 *   - Transport error → failure recorded, backoff applied
 *   - Multiple consecutive failures increase backoff exponentially
 *   - Successful upload after failures resets consecutiveFailureCount and nextRetryAt
 *   - calculateBackoffMs: 0 failures → 0ms
 *   - calculateBackoffMs: 1-4 failures → 1s, 2s, 4s, 8s
 *   - calculateBackoffMs: capped at maxBackoffMs
 *   - updateLastUploadAt: SyncState upserted with current timestamp
 *   - updateLastUploadAt: SyncState created when none exists
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class CloudUploadWorkerTest {

    private val bufferManager: TransactionBufferManager = mockk(relaxed = true)
    private val syncStateDao: SyncStateDao = mockk(relaxed = true)
    private val cloudApiClient: CloudApiClient = mockk()
    private val tokenProvider: DeviceTokenProvider = mockk()

    private val config = CloudUploadWorkerConfig(
        uploadBatchSize = 10,
        baseBackoffMs = 1_000L,
        maxBackoffMs = 60_000L,
    )

    private lateinit var worker: CloudUploadWorker

    @Before
    fun setUp() {
        worker = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            config = config,
        )
        // Default: not decommissioned, has a token
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
        w.uploadPendingBatch()
        coVerify(exactly = 0) { cloudApiClient.uploadBatch(any(), any()) }
    }

    @Test
    fun `returns early when cloudApiClient is null`() = runTest {
        val w = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = null,
            tokenProvider = tokenProvider,
        )
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(makeTransaction())
        w.uploadPendingBatch()
        // No interaction with any cloud client
    }

    @Test
    fun `returns early when tokenProvider is null`() = runTest {
        val w = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = null,
        )
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(makeTransaction())
        w.uploadPendingBatch()
        coVerify(exactly = 0) { cloudApiClient.uploadBatch(any(), any()) }
    }

    @Test
    fun `returns early when device is decommissioned`() = runTest {
        every { tokenProvider.isDecommissioned() } returns true
        worker.uploadPendingBatch()
        coVerify(exactly = 0) { bufferManager.getPendingBatch(any()) }
        coVerify(exactly = 0) { cloudApiClient.uploadBatch(any(), any()) }
    }

    @Test
    fun `returns early when backoff is active`() = runTest {
        worker.nextRetryAt = Instant.now().plusSeconds(60)
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(makeTransaction())
        worker.uploadPendingBatch()
        coVerify(exactly = 0) { cloudApiClient.uploadBatch(any(), any()) }
    }

    @Test
    fun `returns early when no PENDING records`() = runTest {
        coEvery { bufferManager.getPendingBatch(any()) } returns emptyList()
        worker.uploadPendingBatch()
        coVerify(exactly = 0) { cloudApiClient.uploadBatch(any(), any()) }
    }

    @Test
    fun `returns early when access token is null`() = runTest {
        every { tokenProvider.getAccessToken() } returns null
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(makeTransaction())
        worker.uploadPendingBatch()
        coVerify(exactly = 0) { cloudApiClient.uploadBatch(any(), any()) }
    }

    // -------------------------------------------------------------------------
    // Successful uploads — ACCEPTED / DUPLICATE
    // -------------------------------------------------------------------------

    @Test
    fun `ACCEPTED outcome marks record as UPLOADED`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
        )

        worker.uploadPendingBatch()

        coVerify { bufferManager.markUploaded(listOf(tx.id)) }
        assertEquals(0, worker.consecutiveFailureCount)
        assertEquals(Instant.EPOCH, worker.nextRetryAt)
    }

    @Test
    fun `DUPLICATE outcome marks record as UPLOADED (per spec §5_3)`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(listOf(makeResult(tx.fccTransactionId, "DUPLICATE"))),
        )

        worker.uploadPendingBatch()

        coVerify { bufferManager.markUploaded(listOf(tx.id)) }
    }

    @Test
    fun `mixed ACCEPTED and DUPLICATE batch marks all as UPLOADED`() = runTest {
        val tx1 = makeTransaction()
        val tx2 = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx1, tx2)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(
                listOf(
                    makeResult(tx1.fccTransactionId, "ACCEPTED"),
                    makeResult(tx2.fccTransactionId, "DUPLICATE"),
                ),
                acceptedCount = 1,
                duplicateCount = 1,
            ),
        )

        worker.uploadPendingBatch()

        val idsSlot = slot<List<String>>()
        coVerify { bufferManager.markUploaded(capture(idsSlot)) }
        assertTrue(idsSlot.captured.containsAll(listOf(tx1.id, tx2.id)))
    }

    @Test
    fun `successful upload updates SyncState_lastUploadAt`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
        )
        val stateSlot = slot<SyncState>()
        coEvery { syncStateDao.upsert(capture(stateSlot)) } returns Unit

        worker.uploadPendingBatch()

        coVerify { syncStateDao.upsert(any()) }
        assertNotNull(stateSlot.captured.lastUploadAt)
    }

    @Test
    fun `successful upload creates SyncState when none exists`() = runTest {
        coEvery { syncStateDao.get() } returns null
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
        )

        worker.uploadPendingBatch()

        val stateSlot = slot<SyncState>()
        coVerify { syncStateDao.upsert(capture(stateSlot)) }
        assertNotNull(stateSlot.captured.lastUploadAt)
        assertEquals(1, stateSlot.captured.id)
    }

    // -------------------------------------------------------------------------
    // REJECTED outcome
    // -------------------------------------------------------------------------

    @Test
    fun `REJECTED outcome calls recordUploadFailure and leaves record PENDING`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(
                listOf(
                    makeResult(
                        tx.fccTransactionId,
                        "REJECTED",
                        error = CloudErrorResponse("VALIDATION_ERROR", "Missing field"),
                    ),
                ),
                rejectedCount = 1,
            ),
        )

        worker.uploadPendingBatch()

        coVerify(exactly = 0) { bufferManager.markUploaded(any()) }
        coVerify {
            bufferManager.recordUploadFailure(
                id = tx.id,
                attempts = tx.uploadAttempts + 1,
                attemptAt = any(),
                error = match { it.contains("VALIDATION_ERROR") },
            )
        }
        // REJECTED is a per-record cloud decision, not a transport failure — no global backoff
        assertEquals(0, worker.consecutiveFailureCount)
    }

    // -------------------------------------------------------------------------
    // 401 — token refresh
    // -------------------------------------------------------------------------

    @Test
    fun `401 triggers token refresh and retry, marks UPLOADED on success`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { tokenProvider.refreshAccessToken() } returns true
        every { tokenProvider.getAccessToken() } returnsMany listOf("old-token", "new-token")

        // First call returns 401; retry with new token returns success
        coEvery { cloudApiClient.uploadBatch(any(), "old-token") } returns CloudUploadResult.Unauthorized
        coEvery { cloudApiClient.uploadBatch(any(), "new-token") } returns CloudUploadResult.Success(
            makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
        )

        worker.uploadPendingBatch()

        coVerify { tokenProvider.refreshAccessToken() }
        coVerify { bufferManager.markUploaded(listOf(tx.id)) }
        assertEquals(0, worker.consecutiveFailureCount)
    }

    @Test
    fun `401 records failure when refreshAccessToken returns false`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns false

        worker.uploadPendingBatch()

        coVerify { tokenProvider.refreshAccessToken() }
        coVerify(exactly = 0) { bufferManager.markUploaded(any()) }
        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.EPOCH))
    }

    @Test
    fun `401 after successful refresh still records failure`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { tokenProvider.refreshAccessToken() } returns true
        every { tokenProvider.getAccessToken() } returnsMany listOf("old-token", "new-token")

        // Both calls return 401
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Unauthorized

        worker.uploadPendingBatch()

        coVerify(exactly = 0) { bufferManager.markUploaded(any()) }
        assertEquals(1, worker.consecutiveFailureCount)
    }

    // -------------------------------------------------------------------------
    // 403 — decommission and other forbidden
    // -------------------------------------------------------------------------

    @Test
    fun `403 DEVICE_DECOMMISSIONED calls markDecommissioned and stops sync`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.Forbidden("DEVICE_DECOMMISSIONED")

        worker.uploadPendingBatch()

        coVerify { tokenProvider.markDecommissioned() }
        coVerify(exactly = 0) { bufferManager.markUploaded(any()) }
        // Decommission is permanent — no backoff delay set (it is irrelevant)
        assertEquals(0, worker.consecutiveFailureCount)
    }

    @Test
    fun `403 non-decommission records failure with backoff`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.Forbidden("SITE_MISMATCH")

        worker.uploadPendingBatch()

        coVerify(exactly = 0) { tokenProvider.markDecommissioned() }
        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.EPOCH))
    }

    // -------------------------------------------------------------------------
    // Transport errors and backoff
    // -------------------------------------------------------------------------

    @Test
    fun `transport error increments failure count and sets backoff`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.TransportError("Connection timed out")

        worker.uploadPendingBatch()

        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.EPOCH))
        coVerify {
            bufferManager.recordUploadFailure(
                id = tx.id,
                attempts = 1,
                attemptAt = any(),
                error = match { it.contains("Connection timed out") },
            )
        }
    }

    @Test
    fun `multiple consecutive failures increase failure count`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.TransportError("Network error")

        worker.uploadPendingBatch()
        // Reset backoff guard to allow second attempt
        worker.nextRetryAt = Instant.EPOCH
        worker.uploadPendingBatch()

        assertEquals(2, worker.consecutiveFailureCount)
    }

    @Test
    fun `successful upload after failures resets consecutiveFailureCount and nextRetryAt`() = runTest {
        // Seed failure state
        worker.consecutiveFailureCount = 3
        worker.nextRetryAt = Instant.EPOCH // expired backoff

        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
        )

        worker.uploadPendingBatch()

        assertEquals(0, worker.consecutiveFailureCount)
        assertEquals(Instant.EPOCH, worker.nextRetryAt)
    }

    // -------------------------------------------------------------------------
    // Backoff calculation
    // -------------------------------------------------------------------------

    @Test
    fun `calculateBackoffMs returns 0 for zero failures`() {
        assertEquals(0L, worker.calculateBackoffMs(0))
    }

    @Test
    fun `calculateBackoffMs returns 0 for negative failures`() {
        assertEquals(0L, worker.calculateBackoffMs(-1))
    }

    @Test
    fun `calculateBackoffMs doubles with each failure up to max`() {
        assertEquals(1_000L, worker.calculateBackoffMs(1))
        assertEquals(2_000L, worker.calculateBackoffMs(2))
        assertEquals(4_000L, worker.calculateBackoffMs(3))
        assertEquals(8_000L, worker.calculateBackoffMs(4))
        assertEquals(16_000L, worker.calculateBackoffMs(5))
        assertEquals(32_000L, worker.calculateBackoffMs(6))
    }

    @Test
    fun `calculateBackoffMs caps at maxBackoffMs`() {
        // 7 failures = 64s which exceeds max of 60s
        assertEquals(60_000L, worker.calculateBackoffMs(7))
        assertEquals(60_000L, worker.calculateBackoffMs(20))
        assertEquals(60_000L, worker.calculateBackoffMs(100))
    }

    // -------------------------------------------------------------------------
    // Upload request construction
    // -------------------------------------------------------------------------

    @Test
    fun `upload request uses batch size from config`() = runTest {
        val transactions = List(15) { makeTransaction() }
        coEvery { bufferManager.getPendingBatch(config.uploadBatchSize) } returns
            transactions.take(config.uploadBatchSize)

        val requestSlot = slot<CloudUploadRequest>()
        coEvery { cloudApiClient.uploadBatch(capture(requestSlot), any()) } returns
            CloudUploadResult.Success(
                makeResponse(
                    transactions.take(config.uploadBatchSize)
                        .map { makeResult(it.fccTransactionId, "ACCEPTED") },
                ),
            )

        worker.uploadPendingBatch()

        coVerify { bufferManager.getPendingBatch(config.uploadBatchSize) }
        assertTrue(requestSlot.captured.transactions.size <= config.uploadBatchSize)
    }

    @Test
    fun `upload request includes legalEntityId from token provider`() = runTest {
        val legalEntityId = "10000000-0000-0000-0000-000000000042"
        every { tokenProvider.getLegalEntityId() } returns legalEntityId
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)

        val requestSlot = slot<CloudUploadRequest>()
        coEvery { cloudApiClient.uploadBatch(capture(requestSlot), any()) } returns
            CloudUploadResult.Success(
                makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
            )

        worker.uploadPendingBatch()

        assertEquals(legalEntityId, requestSlot.captured.transactions.first().legalEntityId)
    }

    @Test
    fun `upload request sets isDuplicate to false for all records`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)

        val requestSlot = slot<CloudUploadRequest>()
        coEvery { cloudApiClient.uploadBatch(capture(requestSlot), any()) } returns
            CloudUploadResult.Success(
                makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
            )

        worker.uploadPendingBatch()

        assertFalse(requestSlot.captured.transactions.first().isDuplicate)
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun makeTransaction(
        id: String = UUID.randomUUID().toString(),
        fccTransactionId: String = "FCC-${UUID.randomUUID()}",
        uploadAttempts: Int = 0,
    ): BufferedTransaction = BufferedTransaction(
        id = id,
        fccTransactionId = fccTransactionId,
        siteCode = "SITE-001",
        pumpNumber = 1,
        nozzleNumber = 1,
        productCode = "PMS",
        volumeMicrolitres = 10_000_000L,
        amountMinorUnits = 5_000L,
        unitPriceMinorPerLitre = 50L,
        currencyCode = "NGN",
        startedAt = "2024-01-01T10:00:00Z",
        completedAt = "2024-01-01T10:01:00Z",
        fiscalReceiptNumber = null,
        fccVendor = "DOMS",
        attendantId = null,
        status = "PENDING",
        syncStatus = "PENDING",
        ingestionSource = "EDGE_UPLOAD",
        rawPayloadJson = null,
        correlationId = UUID.randomUUID().toString(),
        uploadAttempts = uploadAttempts,
        lastUploadAttemptAt = null,
        lastUploadError = null,
        schemaVersion = 1,
        createdAt = "2024-01-01T10:01:05Z",
        updatedAt = "2024-01-01T10:01:05Z",
    )

    private fun makeResult(
        fccTransactionId: String,
        outcome: String,
        error: CloudErrorResponse? = null,
    ): CloudUploadRecordResult = CloudUploadRecordResult(
        fccTransactionId = fccTransactionId,
        siteCode = "SITE-001",
        outcome = outcome,
        id = if (outcome != "REJECTED") UUID.randomUUID().toString() else null,
        error = error,
    )

    private fun makeResponse(
        results: List<CloudUploadRecordResult>,
        acceptedCount: Int = results.count { it.outcome == "ACCEPTED" },
        duplicateCount: Int = results.count { it.outcome == "DUPLICATE" },
        rejectedCount: Int = results.count { it.outcome == "REJECTED" },
    ): CloudUploadResponse = CloudUploadResponse(
        results = results,
        acceptedCount = acceptedCount,
        duplicateCount = duplicateCount,
        rejectedCount = rejectedCount,
    )
}
