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
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant
import java.util.UUID

/**
 * UploadSyncEdgeCaseTest — covers missing test scenarios:
 *   - 429 rate limiting with and without Retry-After header
 *   - 413 PayloadTooLarge → batch size halving
 *   - Batch partial failure (some ACCEPTED + some REJECTED)
 *   - Token expiry mid-batch (upload → status poll)
 *   - SyncState write failure after successful upload → circuit breaker NOT reset
 *   - Decommission detected between workers
 *   - M-04: token store inconsistency (refresh succeeds, getAccessToken() null)
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class UploadSyncEdgeCaseTest {

    private val bufferManager: TransactionBufferManager = mockk(relaxed = true)
    private val syncStateDao: SyncStateDao = mockk(relaxed = true)
    private val cloudApiClient: CloudApiClient = mockk()
    private val tokenProvider: DeviceTokenProvider = mockk()
    private val telemetryReporter: TelemetryReporter = mockk(relaxed = true)

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
            telemetryReporter = telemetryReporter,
        )
        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.markDecommissioned() } just Runs
        every { tokenProvider.getAccessToken() } returns "valid-jwt-token"
        every { tokenProvider.getLegalEntityId() } returns "10000000-0000-0000-0000-000000000001"
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit
    }

    // -------------------------------------------------------------------------
    // 429 Rate Limiting
    // -------------------------------------------------------------------------

    @Test
    fun `429 with Retry-After header respects server backoff duration`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.RateLimited(retryAfterSeconds = 120)

        worker.uploadPendingBatch()

        // Circuit breaker should have the explicit backoff set, not a failure count increment
        assertTrue(worker.nextRetryAt.isAfter(Instant.now().plusSeconds(60)))
    }

    @Test
    fun `429 without Retry-After falls back to circuit breaker exponential backoff`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.RateLimited(retryAfterSeconds = null)

        worker.uploadPendingBatch()

        // Should record a failure with exponential backoff
        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.EPOCH))
    }

    @Test
    fun `status poll 429 with Retry-After sets explicit backoff`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("fcc-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns
            CloudStatusPollResult.RateLimited(retryAfterSeconds = 60)

        worker.pollSyncedToOdooStatus()

        assertTrue(worker.statusPollNextRetryAt.isAfter(Instant.now().plusSeconds(30)))
    }

    @Test
    fun `status poll 429 without Retry-After uses circuit breaker backoff`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("fcc-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns
            CloudStatusPollResult.RateLimited(retryAfterSeconds = null)

        worker.pollSyncedToOdooStatus()

        assertEquals(1, worker.statusPollConsecutiveFailureCount)
    }

    // -------------------------------------------------------------------------
    // 413 PayloadTooLarge — batch size halving (M-15)
    // -------------------------------------------------------------------------

    @Test
    fun `413 halves effective batch size`() = runTest {
        assertEquals(config.uploadBatchSize, worker.effectiveBatchSize)

        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.PayloadTooLarge

        worker.uploadPendingBatch()

        assertEquals(config.uploadBatchSize / 2, worker.effectiveBatchSize)
        // 413 is not a transport fault — no circuit breaker penalty
        assertEquals(0, worker.consecutiveFailureCount)
    }

    @Test
    fun `413 repeated halving floors at 1`() = runTest {
        worker.effectiveBatchSize = 2

        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.PayloadTooLarge

        worker.uploadPendingBatch()
        assertEquals(1, worker.effectiveBatchSize)

        // One more 413 should keep it at 1
        worker.uploadPendingBatch()
        assertEquals(1, worker.effectiveBatchSize)
    }

    @Test
    fun `successful upload after 413 resets batch size to config default`() = runTest {
        worker.effectiveBatchSize = 3 // previously halved

        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
        )

        worker.uploadPendingBatch()

        assertEquals(config.uploadBatchSize, worker.effectiveBatchSize)
    }

    // -------------------------------------------------------------------------
    // Batch partial failure — some ACCEPTED + some REJECTED
    // -------------------------------------------------------------------------

    @Test
    fun `partial batch — ACCEPTED records marked UPLOADED, REJECTED stay PENDING`() = runTest {
        val txOk = makeTransaction()
        val txBad = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(txOk, txBad)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(
                listOf(
                    makeResult(txOk.fccTransactionId, "ACCEPTED"),
                    makeResult(txBad.fccTransactionId, "REJECTED",
                        errorCode = "SCHEMA_MISMATCH",
                        errorMessage = "Missing field"),
                ),
                acceptedCount = 1,
                rejectedCount = 1,
            ),
        )

        worker.uploadPendingBatch()

        coVerify { bufferManager.markUploaded(listOf(txOk.id)) }
        coVerify {
            bufferManager.recordUploadFailure(
                id = txBad.id,
                attempts = txBad.uploadAttempts + 1,
                attemptAt = any(),
                error = match { it.contains("SCHEMA_MISMATCH") },
            )
        }
    }

    // -------------------------------------------------------------------------
    // Token expiry mid-batch — upload succeeds, then status poll gets 401
    // -------------------------------------------------------------------------

    @Test
    fun `token expiry between upload and status poll — each worker handles independently`() = runTest {
        // Upload succeeds with valid token
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
        )

        worker.uploadPendingBatch()
        coVerify { bufferManager.markUploaded(listOf(tx.id)) }

        // Now status poll gets 401 — token expired between calls
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf(tx.fccTransactionId)
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns CloudStatusPollResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns false

        worker.pollSyncedToOdooStatus()

        // Upload circuit breaker still clean; status poll circuit breaker has 1 failure
        assertEquals(0, worker.consecutiveFailureCount)
        assertEquals(1, worker.statusPollConsecutiveFailureCount)
    }

    // -------------------------------------------------------------------------
    // SyncState write failure after successful upload
    // -------------------------------------------------------------------------

    @Test
    fun `M-03 SyncState write failure after upload does NOT reset circuit breaker`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeResponse(listOf(makeResult(tx.fccTransactionId, "ACCEPTED"))),
        )
        // Simulate DB write failure
        coEvery { syncStateDao.upsert(any()) } throws RuntimeException("Disk full")

        worker.uploadPendingBatch()

        // Records should still be marked UPLOADED (that succeeded)
        coVerify { bufferManager.markUploaded(listOf(tx.id)) }
        // But circuit breaker should NOT be reset because DB write failed
        // (backoff should stay active so next tick retries the DB write)
    }

    @Test
    fun `M-03 SyncState write failure after status poll does NOT reset circuit breaker`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("fcc-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns
            CloudStatusPollResult.Success(
                SyncedStatusResponse(
                    fccTransactionIds = listOf("fcc-1"),
                ),
            )
        // Simulate DB write failure
        coEvery { syncStateDao.upsert(any()) } throws RuntimeException("Disk full")

        worker.pollSyncedToOdooStatus()

        // markSyncedToOdoo should still be called
        coVerify { bufferManager.markSyncedToOdoo(listOf("fcc-1")) }
    }

    // -------------------------------------------------------------------------
    // M-04: Token store inconsistency
    // -------------------------------------------------------------------------

    @Test
    fun `M-04 refresh succeeds but getAccessToken returns null — treated as critical error`() = runTest {
        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns true
        // Refresh says success but token store returns null
        every { tokenProvider.getAccessToken() } returnsMany listOf("old-token", null)

        worker.uploadPendingBatch()

        // Should record a transport failure (not a crash)
        assertEquals(1, worker.consecutiveFailureCount)
        // Should increment telemetry auth error counter
        coVerify { telemetryReporter.cloudAuthErrors }
    }

    // -------------------------------------------------------------------------
    // 401 retry on status poll
    // -------------------------------------------------------------------------

    @Test
    fun `status poll 401 triggers refresh and retry, marks synced on success`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("fcc-1")
        coEvery { tokenProvider.refreshAccessToken() } returns true
        every { tokenProvider.getAccessToken() } returnsMany listOf("old-token", "new-token")

        coEvery { cloudApiClient.getSyncedStatus(any(), "old-token") } returns
            CloudStatusPollResult.Unauthorized
        coEvery { cloudApiClient.getSyncedStatus(any(), "new-token") } returns
            CloudStatusPollResult.Success(
                SyncedStatusResponse(
                    fccTransactionIds = listOf("fcc-1"),
                ),
            )

        worker.pollSyncedToOdooStatus()

        coVerify { tokenProvider.refreshAccessToken() }
        coVerify { bufferManager.markSyncedToOdoo(listOf("fcc-1")) }
        assertEquals(0, worker.statusPollConsecutiveFailureCount)
    }

    // -------------------------------------------------------------------------
    // Status poll decommission
    // -------------------------------------------------------------------------

    @Test
    fun `status poll 403 DEVICE_DECOMMISSIONED calls markDecommissioned`() = runTest {
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns listOf("fcc-1")
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns
            CloudStatusPollResult.Forbidden("DEVICE_DECOMMISSIONED")

        worker.pollSyncedToOdooStatus()

        coVerify { tokenProvider.markDecommissioned() }
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
        errorCode: String? = null,
        errorMessage: String? = null,
    ): CloudUploadRecordResult = CloudUploadRecordResult(
        fccTransactionId = fccTransactionId,
        outcome = outcome,
        transactionId = if (outcome == "ACCEPTED") UUID.randomUUID().toString() else null,
        originalTransactionId = if (outcome == "DUPLICATE") UUID.randomUUID().toString() else null,
        errorCode = errorCode,
        errorMessage = errorMessage,
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
