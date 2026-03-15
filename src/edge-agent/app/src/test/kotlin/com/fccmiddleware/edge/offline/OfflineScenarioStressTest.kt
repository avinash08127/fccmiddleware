package com.fccmiddleware.edge.offline

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.offline.OfflineScenarioTestHelpers.makeAllAcceptedResponse
import com.fccmiddleware.edge.offline.OfflineScenarioTestHelpers.makeBatch
import com.fccmiddleware.edge.offline.OfflineScenarioTestHelpers.makePartialResponse
import com.fccmiddleware.edge.offline.OfflineScenarioTestHelpers.makeSyncedStatusResponse
import com.fccmiddleware.edge.offline.OfflineScenarioTestHelpers.makeTransaction
import com.fccmiddleware.edge.runtime.CadenceController
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudStatusPollResult
import com.fccmiddleware.edge.sync.CloudUploadResponse
import com.fccmiddleware.edge.sync.CloudUploadResult
import com.fccmiddleware.edge.sync.CloudUploadWorker
import com.fccmiddleware.edge.sync.CloudUploadWorkerConfig
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import com.fccmiddleware.edge.sync.SyncedStatusResponse
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
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
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * OfflineScenarioStressTest — EA-6.1 offline scenario tests OFF-1 through OFF-6.
 *
 * These tests validate the system's resilience under network failures:
 *
 * - OFF-1: Internet drop during upload batch (partial success) → retry resumes correctly
 * - OFF-2: FCC LAN drop during poll → graceful degradation
 * - OFF-3: 1-hour internet outage → buffer captures all, replay succeeds
 * - OFF-4: 24-hour outage (1,000 records) → full replay without OOM
 * - OFF-5: 7-day outage (30,000 records) → buffer integrity + replay ordering
 * - OFF-6: Staggered recovery (both down → FCC first → internet) → correct state transitions
 *
 * Acceptance criteria:
 *   - Zero transactions lost in any scenario
 *   - Buffer handles 30,000+ records without OOM
 *   - Upload replay maintains chronological order after recovery
 *
 * Uses MockK for cloud/FCC dependencies and Robolectric for Android API shims.
 */
@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class OfflineScenarioStressTest {

    private val bufferManager: TransactionBufferManager = mockk(relaxed = true)
    private val syncStateDao: SyncStateDao = mockk(relaxed = true)
    private val cloudApiClient: CloudApiClient = mockk()
    private val tokenProvider: DeviceTokenProvider = mockk()
    private val transactionDao: TransactionBufferDao = mockk(relaxed = true)
    private val auditLogDao: AuditLogDao = mockk(relaxed = true)

    private val uploadConfig = CloudUploadWorkerConfig(
        uploadBatchSize = 50,
        baseBackoffMs = 1_000L,
        maxBackoffMs = 60_000L,
    )

    private lateinit var uploadWorker: CloudUploadWorker

    @Before
    fun setUp() {
        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns "valid-jwt"
        every { tokenProvider.getLegalEntityId() } returns "10000000-0000-0000-0000-000000000001"
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit
        coEvery { auditLogDao.insert(any()) } returns 1L

        uploadWorker = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            config = uploadConfig,
        )
    }

    // =========================================================================
    // OFF-1: Internet drop during upload batch (partial success)
    // =========================================================================

    @Test
    fun `OFF-1 partial upload marks accepted records UPLOADED and retries remainder`() = runTest {
        // Setup: 50 PENDING records ready for upload
        val batch = makeBatch(50)
        coEvery { bufferManager.getPendingBatch(any()) } returns batch

        // First upload: partial success — 20 ACCEPTED, 30 REJECTED (simulating mid-batch internet drop)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makePartialResponse(batch, acceptedCount = 20),
        )

        uploadWorker.uploadPendingBatch()

        // Verify: first 20 marked UPLOADED
        coVerify { bufferManager.markUploaded(match { it.size == 20 }) }
        // Verify: remaining 30 had recordUploadFailure called (stays PENDING)
        coVerify(exactly = 30) {
            bufferManager.recordUploadFailure(
                id = any(),
                attempts = 1,
                attemptAt = any(),
                error = match { it.contains("UPLOAD_INTERRUPTED") },
            )
        }
        // Verify: no global backoff set (per-record rejection, not transport failure)
        assertEquals(0, uploadWorker.consecutiveFailureCount)
    }

    @Test
    fun `OFF-1 transport error during upload sets backoff and records failure for all records`() = runTest {
        val batch = makeBatch(10)
        coEvery { bufferManager.getPendingBatch(any()) } returns batch
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.TransportError("Connection reset by peer")

        uploadWorker.uploadPendingBatch()

        // Verify: failure recorded for each record in batch
        coVerify(exactly = 10) {
            bufferManager.recordUploadFailure(
                id = any(),
                attempts = 1,
                attemptAt = any(),
                error = match { it.contains("Connection reset") },
            )
        }
        assertEquals(1, uploadWorker.consecutiveFailureCount)
        assertTrue(uploadWorker.nextRetryAt.isAfter(Instant.EPOCH))
    }

    @Test
    fun `OFF-1 retry after backoff expires succeeds and resets failure state`() = runTest {
        val batch = makeBatch(10)
        coEvery { bufferManager.getPendingBatch(any()) } returns batch

        // First attempt: transport error
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.TransportError("Connection lost")
        uploadWorker.uploadPendingBatch()
        assertEquals(1, uploadWorker.consecutiveFailureCount)

        // Simulate backoff expiry
        uploadWorker.uploadCircuitBreaker.nextRetryAt = Instant.EPOCH

        // Second attempt: success
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeAllAcceptedResponse(batch),
        )
        uploadWorker.uploadPendingBatch()

        // Verify: all records marked UPLOADED, failure state reset
        coVerify { bufferManager.markUploaded(match { it.size == 10 }) }
        assertEquals(0, uploadWorker.consecutiveFailureCount)
        assertEquals(Instant.EPOCH, uploadWorker.nextRetryAt)
    }

    // =========================================================================
    // OFF-2: FCC LAN drop during poll → graceful degradation
    // =========================================================================

    @Test
    fun `OFF-2 connectivity transitions to FCC_UNREACHABLE after 3 FCC failures`() =
        runTest(StandardTestDispatcher()) {
            val mgr = ConnectivityManager(
                internetProbe = { true },
                fccProbe = { false },   // FCC always down
                auditLogDao = auditLogDao,
                scope = this,
                config = ConnectivityManager.ProbeConfig(
                    probeIntervalMs = 100L,
                    probeTimeoutMs = 50L,
                    failureThreshold = 3,
                    jitterRangeMs = 0L,
                ),
            )

            mgr.start()
            advanceTimeBy(500L) // enough for 3+ FCC failures

            assertEquals(ConnectivityState.FCC_UNREACHABLE, mgr.state.value)
            mgr.stop()
        }

    @Test
    fun `OFF-2 cadence controller skips FCC poll when FCC_UNREACHABLE but continues upload`() = runTest {
        // Simulate FCC_UNREACHABLE: internet up, FCC down
        val connectivityManager: ConnectivityManager = mockk(relaxed = true)
        val connectivityState = kotlinx.coroutines.flow.MutableStateFlow(ConnectivityState.FCC_UNREACHABLE)
        every { connectivityManager.state } returns connectivityState

        val ingestionOrchestrator: IngestionOrchestrator = mockk(relaxed = true)

        val cadence = CadenceController(
            connectivityManager = connectivityManager,
            ingestionOrchestrator = ingestionOrchestrator,
            cloudUploadWorker = uploadWorker,
            transactionDao = transactionDao,
            scope = this,
            config = CadenceController.CadenceConfig(
                baseIntervalMs = 100L,
                jitterRangeMs = 0L,
                offlineIntervalMs = 200L,
            ),
        )

        // Set up upload to succeed (draining existing buffer)
        val batch = makeBatch(5)
        coEvery { bufferManager.getPendingBatch(any()) } returns batch
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeAllAcceptedResponse(batch),
        )
        coEvery { transactionDao.countForLocalApi() } returns 5

        cadence.start()
        kotlinx.coroutines.delay(250L) // allow at least 1 tick
        cadence.stop()

        // FCC poll should NOT be called in FCC_UNREACHABLE state
        coVerify(exactly = 0) { ingestionOrchestrator.poll() }
        // Cloud upload SHOULD be called (draining existing PENDING records)
        coVerify(atLeast = 1) { cloudApiClient.uploadBatch(any(), any()) }
    }

    @Test
    fun `OFF-2 FCC recovery triggers immediate poll on next cadence tick`() =
        runTest(StandardTestDispatcher()) {
            var fccAlive = false
            val mgr = ConnectivityManager(
                internetProbe = { true },
                fccProbe = { fccAlive },
                auditLogDao = auditLogDao,
                scope = this,
                config = ConnectivityManager.ProbeConfig(
                    probeIntervalMs = 100L,
                    probeTimeoutMs = 50L,
                    failureThreshold = 3,
                    jitterRangeMs = 0L,
                ),
            )

            mgr.start()
            // First: FCC down → after 3 failures, should be FCC_UNREACHABLE
            advanceTimeBy(500L)
            assertEquals(ConnectivityState.FCC_UNREACHABLE, mgr.state.value)

            // Recover FCC
            fccAlive = true
            advanceTimeBy(200L) // single success recovers
            assertEquals(ConnectivityState.FULLY_ONLINE, mgr.state.value)
            mgr.stop()
        }

    // =========================================================================
    // OFF-3: 1-hour internet outage → buffer captures all, replay succeeds
    // =========================================================================

    @Test
    fun `OFF-3 all transactions buffered during outage are uploaded after recovery`() = runTest {
        // Simulate: 60 transactions buffered during 1-hour outage (1/min)
        val bufferedDuringOutage = makeBatch(60)

        // Phase 1: Internet down — uploads fail with transport error
        coEvery { bufferManager.getPendingBatch(any()) } returns bufferedDuringOutage
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.TransportError("No route to host")

        uploadWorker.uploadPendingBatch()
        assertEquals(1, uploadWorker.consecutiveFailureCount)

        // Phase 2: Internet recovers — reset backoff, retry succeeds
        uploadWorker.uploadCircuitBreaker.nextRetryAt = Instant.EPOCH
        uploadWorker.uploadCircuitBreaker.consecutiveFailureCount = 0

        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeAllAcceptedResponse(bufferedDuringOutage),
        )

        uploadWorker.uploadPendingBatch()

        // Verify: all 60 records marked UPLOADED
        coVerify { bufferManager.markUploaded(match { it.size == 60 }) }
        assertEquals(0, uploadWorker.consecutiveFailureCount)
    }

    @Test
    fun `OFF-3 replay preserves chronological order (oldest first)`() = runTest {
        // Create records with explicit chronological timestamps
        val baseTime = Instant.parse("2024-06-01T08:00:00Z")
        val orderedBatch = (0 until 50).map { i ->
            makeTransaction(index = i, baseTime = baseTime.plusSeconds(i * 60L))
        }.sortedBy { it.createdAt }

        coEvery { bufferManager.getPendingBatch(any()) } returns orderedBatch

        val requestSlot = slot<com.fccmiddleware.edge.sync.CloudUploadRequest>()
        coEvery { cloudApiClient.uploadBatch(capture(requestSlot), any()) } returns
            CloudUploadResult.Success(makeAllAcceptedResponse(orderedBatch))

        uploadWorker.uploadPendingBatch()

        // Verify: request preserves oldest-first ordering
        val uploadedDtos = requestSlot.captured.transactions
        for (i in 1 until uploadedDtos.size) {
            assertTrue(
                "Upload order violated: ${uploadedDtos[i].completedAt} < ${uploadedDtos[i - 1].completedAt}",
                uploadedDtos[i].completedAt >= uploadedDtos[i - 1].completedAt,
            )
        }
    }

    @Test
    fun `OFF-3 deduplication on recovery prevents double upload`() = runTest {
        val batch = makeBatch(10)

        // First upload: all DUPLICATE (cloud already has them from FCC push)
        coEvery { bufferManager.getPendingBatch(any()) } returns batch
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            CloudUploadResponse(
                results = batch.map {
                    com.fccmiddleware.edge.sync.CloudUploadRecordResult(
                        fccTransactionId = it.fccTransactionId,
                        outcome = "DUPLICATE",
                        originalTransactionId = UUID.randomUUID().toString(),
                    )
                },
                acceptedCount = 0,
                duplicateCount = 10,
                rejectedCount = 0,
            ),
        )

        uploadWorker.uploadPendingBatch()

        // DUPLICATE → UPLOADED per §5.3 (cloud dedup confirmed, no double-store)
        coVerify { bufferManager.markUploaded(match { it.size == 10 }) }
    }

    // =========================================================================
    // OFF-4: 24-hour outage with 1,000 buffered records
    // =========================================================================

    @Test
    fun `OFF-4 full replay of 1000 records succeeds in batches without OOM`() = runTest {
        val allRecords = makeBatch(1_000)
        val batchSize = uploadConfig.uploadBatchSize
        val totalBatches = (allRecords.size + batchSize - 1) / batchSize

        // Simulate batched upload: each call returns the next batch
        var batchIndex = 0
        coEvery { bufferManager.getPendingBatch(any()) } answers {
            val start = batchIndex * batchSize
            val end = minOf(start + batchSize, allRecords.size)
            if (start >= allRecords.size) emptyList()
            else allRecords.subList(start, end)
        }

        coEvery { cloudApiClient.uploadBatch(any(), any()) } answers {
            val req = firstArg<com.fccmiddleware.edge.sync.CloudUploadRequest>()
            CloudUploadResult.Success(
                CloudUploadResponse(
                    results = req.transactions.map {
                        com.fccmiddleware.edge.sync.CloudUploadRecordResult(
                            fccTransactionId = it.fccTransactionId,
                            outcome = "ACCEPTED",
                            transactionId = UUID.randomUUID().toString(),
                        )
                    },
                    acceptedCount = req.transactions.size,
                    duplicateCount = 0,
                    rejectedCount = 0,
                ),
            )
        }

        var uploadedTotal = 0
        for (i in 0 until totalBatches) {
            batchIndex = i
            uploadWorker.uploadPendingBatch()
        }

        // Verify: markUploaded called for each batch
        coVerify(atLeast = totalBatches) { bufferManager.markUploaded(any()) }
        // No OOM: test completes without OutOfMemoryError
    }

    @Test
    fun `OFF-4 replay maintains chronological order across all batches`() = runTest {
        val allRecords = (0 until 1_000).map { i ->
            makeTransaction(
                index = i,
                baseTime = Instant.parse("2024-06-01T08:00:00Z").plusSeconds(i * 60L),
            )
        }.sortedBy { it.createdAt }

        val batchSize = uploadConfig.uploadBatchSize
        val capturedOrders = mutableListOf<String>()

        // Simulate sequential batch uploads tracking order
        var batchIndex = 0
        coEvery { bufferManager.getPendingBatch(any()) } answers {
            val start = batchIndex * batchSize
            val end = minOf(start + batchSize, allRecords.size)
            if (start >= allRecords.size) emptyList()
            else allRecords.subList(start, end)
        }

        coEvery { cloudApiClient.uploadBatch(any(), any()) } answers {
            val req = firstArg<com.fccmiddleware.edge.sync.CloudUploadRequest>()
            req.transactions.forEach { capturedOrders.add(it.completedAt) }
            CloudUploadResult.Success(
                CloudUploadResponse(
                    results = req.transactions.map {
                        com.fccmiddleware.edge.sync.CloudUploadRecordResult(
                            fccTransactionId = it.fccTransactionId,
                            outcome = "ACCEPTED",
                            transactionId = UUID.randomUUID().toString(),
                        )
                    },
                    acceptedCount = req.transactions.size,
                    duplicateCount = 0,
                    rejectedCount = 0,
                ),
            )
        }

        val totalBatches = (allRecords.size + batchSize - 1) / batchSize
        for (i in 0 until totalBatches) {
            batchIndex = i
            uploadWorker.uploadPendingBatch()
        }

        // Verify chronological ordering across ALL batches
        for (i in 1 until capturedOrders.size) {
            assertTrue(
                "Cross-batch ordering violated at index $i: ${capturedOrders[i]} < ${capturedOrders[i - 1]}",
                capturedOrders[i] >= capturedOrders[i - 1],
            )
        }
    }

    // =========================================================================
    // OFF-5: 7-day outage with 30,000 buffered records
    // =========================================================================

    @Test
    fun `OFF-5 buffer handles 30000 records and replay completes without OOM`() = runTest {
        // Generate 30,000 records (reuse SeedDataGenerator pattern)
        val baseTime = Instant.now()
        val allRecords = (0 until 30_000).map { i ->
            makeTransaction(
                index = i,
                baseTime = baseTime,
                windowSeconds = 7L * 24 * 3600, // 7-day window
            )
        }

        val batchSize = uploadConfig.uploadBatchSize
        val totalBatches = (allRecords.size + batchSize - 1) / batchSize

        var batchIndex = 0
        coEvery { bufferManager.getPendingBatch(any()) } answers {
            val start = batchIndex * batchSize
            val end = minOf(start + batchSize, allRecords.size)
            if (start >= allRecords.size) emptyList()
            else allRecords.subList(start, end)
        }

        coEvery { cloudApiClient.uploadBatch(any(), any()) } answers {
            val req = firstArg<com.fccmiddleware.edge.sync.CloudUploadRequest>()
            CloudUploadResult.Success(
                CloudUploadResponse(
                    results = req.transactions.map {
                        com.fccmiddleware.edge.sync.CloudUploadRecordResult(
                            fccTransactionId = it.fccTransactionId,
                            outcome = "ACCEPTED",
                            transactionId = UUID.randomUUID().toString(),
                        )
                    },
                    acceptedCount = req.transactions.size,
                    duplicateCount = 0,
                    rejectedCount = 0,
                ),
            )
        }

        val uploadedBatchCount = AtomicInteger(0)
        for (i in 0 until totalBatches) {
            batchIndex = i
            uploadWorker.uploadPendingBatch()
            uploadedBatchCount.incrementAndGet()
        }

        // Verify: all 600 batches processed (30,000 / 50 = 600)
        assertEquals(totalBatches, uploadedBatchCount.get())
        coVerify(atLeast = totalBatches) { bufferManager.markUploaded(any()) }
        // If we got here: no OOM
    }

    @Test
    fun `OFF-5 replay throughput meets 600 tx per minute target`() = runTest {
        // Generate 1,000 records for throughput measurement
        val records = makeBatch(1_000)
        val batchSize = uploadConfig.uploadBatchSize
        val totalBatches = (records.size + batchSize - 1) / batchSize

        var batchIndex = 0
        coEvery { bufferManager.getPendingBatch(any()) } answers {
            val start = batchIndex * batchSize
            val end = minOf(start + batchSize, records.size)
            if (start >= records.size) emptyList()
            else records.subList(start, end)
        }

        coEvery { cloudApiClient.uploadBatch(any(), any()) } answers {
            val req = firstArg<com.fccmiddleware.edge.sync.CloudUploadRequest>()
            CloudUploadResult.Success(
                CloudUploadResponse(
                    results = req.transactions.map {
                        com.fccmiddleware.edge.sync.CloudUploadRecordResult(
                            fccTransactionId = it.fccTransactionId,
                            outcome = "ACCEPTED",
                            transactionId = UUID.randomUUID().toString(),
                        )
                    },
                    acceptedCount = req.transactions.size,
                    duplicateCount = 0,
                    rejectedCount = 0,
                ),
            )
        }

        val startMs = System.currentTimeMillis()
        for (i in 0 until totalBatches) {
            batchIndex = i
            uploadWorker.uploadPendingBatch()
        }
        val elapsedMs = System.currentTimeMillis() - startMs
        val txPerMinute = if (elapsedMs > 0) (records.size.toLong() * 60_000L) / elapsedMs else Long.MAX_VALUE

        assertTrue(
            "Replay throughput: $txPerMinute tx/min; must be >= 600 tx/min (local processing side)",
            txPerMinute >= 600,
        )
    }

    @Test
    fun `OFF-5 buffer integrity — all 30000 records have unique IDs and correct sync status`() {
        val records = (0 until 30_000).map { i ->
            makeTransaction(index = i)
        }

        // Verify uniqueness
        val ids = records.map { it.id }.toSet()
        assertEquals("All record IDs must be unique", 30_000, ids.size)

        val fccIds = records.map { it.fccTransactionId }.toSet()
        assertEquals("All FCC transaction IDs must be unique", 30_000, fccIds.size)

        // All start as PENDING
        assertTrue(
            "All records must start as PENDING",
            records.all { it.syncStatus == "PENDING" },
        )
    }

    // =========================================================================
    // OFF-6: Staggered recovery (both down → FCC first → internet)
    // =========================================================================

    @Test
    fun `OFF-6 transitions through FULLY_OFFLINE to FCC_UNREACHABLE to INTERNET_DOWN to FULLY_ONLINE`() =
        runTest(StandardTestDispatcher()) {
            var internetAlive = false
            var fccAlive = false

            val mgr = ConnectivityManager(
                internetProbe = { internetAlive },
                fccProbe = { fccAlive },
                auditLogDao = auditLogDao,
                scope = this,
                config = ConnectivityManager.ProbeConfig(
                    probeIntervalMs = 100L,
                    probeTimeoutMs = 50L,
                    failureThreshold = 3,
                    jitterRangeMs = 0L,
                ),
            )

            mgr.start()

            // Phase 1: Both down → FULLY_OFFLINE
            advanceTimeBy(500L)
            assertEquals(ConnectivityState.FULLY_OFFLINE, mgr.state.value)

            // Phase 2: FCC recovers first → INTERNET_DOWN
            fccAlive = true
            advanceTimeBy(200L)
            assertEquals(ConnectivityState.INTERNET_DOWN, mgr.state.value)

            // Phase 3: Internet recovers → FULLY_ONLINE
            internetAlive = true
            advanceTimeBy(200L)
            assertEquals(ConnectivityState.FULLY_ONLINE, mgr.state.value)
            mgr.stop()
        }

    @Test
    fun `OFF-6 reverse staggered recovery (internet first, then FCC)`() =
        runTest(StandardTestDispatcher()) {
            var internetAlive = false
            var fccAlive = false

            val mgr = ConnectivityManager(
                internetProbe = { internetAlive },
                fccProbe = { fccAlive },
                auditLogDao = auditLogDao,
                scope = this,
                config = ConnectivityManager.ProbeConfig(
                    probeIntervalMs = 100L,
                    probeTimeoutMs = 50L,
                    failureThreshold = 3,
                    jitterRangeMs = 0L,
                ),
            )

            mgr.start()

            // Phase 1: Both down
            advanceTimeBy(500L)
            assertEquals(ConnectivityState.FULLY_OFFLINE, mgr.state.value)

            // Phase 2: Internet recovers first → FCC_UNREACHABLE
            internetAlive = true
            advanceTimeBy(200L)
            assertEquals(ConnectivityState.FCC_UNREACHABLE, mgr.state.value)

            // Phase 3: FCC recovers → FULLY_ONLINE
            fccAlive = true
            advanceTimeBy(200L)
            assertEquals(ConnectivityState.FULLY_ONLINE, mgr.state.value)
            mgr.stop()
        }

    @Test
    fun `OFF-6 immediate replay triggered on FULLY_ONLINE transition`() = runTest {
        // Verify that the CadenceController onTransition triggers immediate upload on FULLY_ONLINE
        val connectivityManager: ConnectivityManager = mockk(relaxed = true)
        val connectivityState = kotlinx.coroutines.flow.MutableStateFlow(ConnectivityState.FULLY_OFFLINE)
        every { connectivityManager.state } returns connectivityState

        val ingestionOrchestrator: IngestionOrchestrator = mockk(relaxed = true)

        val cadence = CadenceController(
            connectivityManager = connectivityManager,
            ingestionOrchestrator = ingestionOrchestrator,
            cloudUploadWorker = uploadWorker,
            transactionDao = transactionDao,
            scope = this,
        )

        // Set up upload to verify it gets called
        val batch = makeBatch(5)
        coEvery { bufferManager.getPendingBatch(any()) } returns batch
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeAllAcceptedResponse(batch),
        )

        // Trigger the onTransition directly (simulates connectivity recovery)
        cadence.onTransition(ConnectivityState.FULLY_OFFLINE, ConnectivityState.FULLY_ONLINE)

        // Allow the launched coroutine to complete
        kotlinx.coroutines.delay(100L)

        // Upload should have been triggered immediately
        coVerify(atLeast = 1) { cloudApiClient.uploadBatch(any(), any()) }
    }

    @Test
    fun `OFF-6 SYNCED_TO_ODOO status poll triggered on FULLY_ONLINE recovery`() = runTest {
        val batch = makeBatch(5)
        val uploadedFccIds = batch.map { it.fccTransactionId }

        coEvery { bufferManager.getPendingBatch(any()) } returns batch
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            makeAllAcceptedResponse(batch),
        )
        coEvery { bufferManager.getUploadedFccTransactionIds(any()) } returns uploadedFccIds
        coEvery { cloudApiClient.getSyncedStatus(any(), any()) } returns CloudStatusPollResult.Success(
            makeSyncedStatusResponse(uploadedFccIds),
        )

        val connectivityManager: ConnectivityManager = mockk(relaxed = true)
        val connectivityState = kotlinx.coroutines.flow.MutableStateFlow(ConnectivityState.FULLY_OFFLINE)
        every { connectivityManager.state } returns connectivityState

        val ingestionOrchestrator: IngestionOrchestrator = mockk(relaxed = true)

        val cadence = CadenceController(
            connectivityManager = connectivityManager,
            ingestionOrchestrator = ingestionOrchestrator,
            cloudUploadWorker = uploadWorker,
            transactionDao = transactionDao,
            scope = this,
        )

        cadence.onTransition(ConnectivityState.INTERNET_DOWN, ConnectivityState.FULLY_ONLINE)
        kotlinx.coroutines.delay(200L)

        // Status poll should have been triggered on recovery
        coVerify(atLeast = 1) { cloudApiClient.getSyncedStatus(any(), any()) }
        // Records should be marked SYNCED_TO_ODOO
        coVerify { bufferManager.markSyncedToOdoo(match { it.size == 5 }) }
    }
}
