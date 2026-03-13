package com.fccmiddleware.edge.offline

import com.fccmiddleware.edge.adapter.common.AgentFccConfig
import com.fccmiddleware.edge.adapter.common.CanonicalTransaction
import com.fccmiddleware.edge.adapter.common.FccVendor
import com.fccmiddleware.edge.adapter.common.FetchCursor
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.IngestionMode
import com.fccmiddleware.edge.adapter.common.IngestionSource
import com.fccmiddleware.edge.adapter.common.TransactionBatch
import com.fccmiddleware.edge.adapter.common.TransactionStatus
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.sync.CircuitBreaker
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudUploadResponse
import com.fccmiddleware.edge.sync.CloudUploadRecordResult
import com.fccmiddleware.edge.sync.CloudUploadRequest
import com.fccmiddleware.edge.sync.CloudUploadResult
import com.fccmiddleware.edge.sync.CloudUploadWorker
import com.fccmiddleware.edge.sync.CloudUploadWorkerConfig
import com.fccmiddleware.edge.sync.DeviceTokenProvider
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
 * OfflineCrashRecoveryTest — validates crash resilience and offline recovery:
 *   - Crash mid-upload: records stay PENDING for retry
 *   - Partial sync state loss: SyncState DAO failure does not corrupt cursor
 *   - IngestionOrchestrator: cursor persist failure → breaks loop, next poll re-fetches
 *   - IngestionOrchestrator: adapter exception → caught gracefully
 *   - CircuitBreaker: reset on connectivity recovery after long OPEN state
 *   - CloudUploadWorker: transport error records failure on every batch record
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class OfflineCrashRecoveryTest {

    // -------------------------------------------------------------------------
    // CloudUploadWorker — crash mid-upload simulation
    // -------------------------------------------------------------------------

    @Test
    fun `transport error during upload leaves all batch records PENDING for retry`() = runTest {
        val bufferManager: TransactionBufferManager = mockk(relaxed = true)
        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val cloudApiClient: CloudApiClient = mockk()
        val tokenProvider: DeviceTokenProvider = mockk()

        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns "token"
        every { tokenProvider.getLegalEntityId() } returns "lei-1"
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit

        val worker = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )

        val tx1 = makeTransaction("tx-1")
        val tx2 = makeTransaction("tx-2")
        val tx3 = makeTransaction("tx-3")
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx1, tx2, tx3)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.TransportError("Connection reset by peer")

        worker.uploadPendingBatch()

        // All 3 records should have failure recorded (they all stay PENDING)
        coVerify { bufferManager.recordUploadFailure(id = "tx-1", attempts = 1, attemptAt = any(), error = any()) }
        coVerify { bufferManager.recordUploadFailure(id = "tx-2", attempts = 1, attemptAt = any(), error = any()) }
        coVerify { bufferManager.recordUploadFailure(id = "tx-3", attempts = 1, attemptAt = any(), error = any()) }
        // No records should be marked UPLOADED
        coVerify(exactly = 0) { bufferManager.markUploaded(any()) }
    }

    @Test
    fun `records PENDING after transport error are retried on next upload call`() = runTest {
        val bufferManager: TransactionBufferManager = mockk(relaxed = true)
        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val cloudApiClient: CloudApiClient = mockk()
        val tokenProvider: DeviceTokenProvider = mockk()

        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns "token"
        every { tokenProvider.getLegalEntityId() } returns "lei-1"
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit

        val worker = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )

        val tx = makeTransaction("tx-retry", uploadAttempts = 1) // was already tried once
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)

        // First call fails
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns
            CloudUploadResult.TransportError("Timeout")
        worker.uploadPendingBatch()
        assertEquals(1, worker.consecutiveFailureCount)

        // Reset backoff for test
        worker.uploadCircuitBreaker.resetOnConnectivityRecovery()

        // Second call succeeds
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            CloudUploadResponse(
                results = listOf(
                    CloudUploadRecordResult(
                        fccTransactionId = tx.fccTransactionId,
                        outcome = "ACCEPTED",
                        transactionId = UUID.randomUUID().toString(),
                    ),
                ),
                acceptedCount = 1,
                duplicateCount = 0,
                rejectedCount = 0,
            ),
        )
        worker.uploadPendingBatch()

        // Now it should be marked UPLOADED
        coVerify { bufferManager.markUploaded(listOf("tx-retry")) }
        assertEquals(0, worker.consecutiveFailureCount)
    }

    // -------------------------------------------------------------------------
    // IngestionOrchestrator — cursor persist failure
    // -------------------------------------------------------------------------

    @Test
    fun `cursor persist failure breaks poll loop — next poll re-fetches same batch`() = runTest {
        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val bufferManager: TransactionBufferManager = mockk(relaxed = true)
        val adapter: IFccAdapter = mockk()

        coEvery { syncStateDao.get() } returns null // no prior cursor
        // First upsert fails (simulate disk full)
        coEvery { syncStateDao.upsert(any()) } throws RuntimeException("Disk full")
        coEvery { bufferManager.bufferTransaction(any()) } returns true

        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = listOf(makeCanonicalTransaction("FCC-001")),
            hasMore = true, // would continue if cursor persisted
            nextCursorToken = "cursor-v2",
        )

        val fccConfig = makeFccConfig()
        val orchestrator = IngestionOrchestrator(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
        ).also { it.wireRuntime(adapter, fccConfig) }

        val result = orchestrator.pollNow()

        // Transaction should still be buffered (it was fetched before cursor persist failed)
        coVerify { bufferManager.bufferTransaction(any()) }
        assertEquals(1, result?.newCount)
        // Cursor persist failed and break executes before fetchCycles++ — counter stays at 0.
        // hasMore=true is not pursued because the loop broke out.
        assertEquals(0, result?.fetchCycles)
    }

    @Test
    fun `SyncState read failure aborts poll gracefully`() = runTest {
        val syncStateDao: SyncStateDao = mockk()
        val bufferManager: TransactionBufferManager = mockk(relaxed = true)
        val adapter: IFccAdapter = mockk()

        coEvery { syncStateDao.get() } throws RuntimeException("DB locked")

        val fccConfig = makeFccConfig()
        val orchestrator = IngestionOrchestrator(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
        ).also { it.wireRuntime(adapter, fccConfig) }

        val result = orchestrator.pollNow()

        // Should return empty result without crashing
        assertEquals(0, result?.newCount)
        assertEquals(0, result?.fetchCycles)
        // Adapter should never be called
        coVerify(exactly = 0) { adapter.fetchTransactions(any()) }
    }

    @Test
    fun `adapter fetchTransactions exception is caught — poll returns partial result`() = runTest {
        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val bufferManager: TransactionBufferManager = mockk(relaxed = true)
        val adapter: IFccAdapter = mockk()

        coEvery { syncStateDao.get() } returns null
        coEvery { adapter.fetchTransactions(any()) } throws RuntimeException("FCC connection refused")

        val fccConfig = makeFccConfig()
        val orchestrator = IngestionOrchestrator(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
        ).also { it.wireRuntime(adapter, fccConfig) }

        val result = orchestrator.pollNow()

        // Should not crash, return 0 results
        assertEquals(0, result?.newCount)
        assertEquals(0, result?.fetchCycles)
    }

    // -------------------------------------------------------------------------
    // CircuitBreaker — recovery after long OPEN state
    // -------------------------------------------------------------------------

    @Test
    fun `circuit breaker recovery from OPEN resets all state`() = runTest {
        val cb = CircuitBreaker(name = "test", openThreshold = 5)

        // Drive to OPEN
        repeat(5) { cb.recordFailure() }
        assertEquals(CircuitBreaker.State.OPEN, cb.state)
        assertEquals(5, cb.consecutiveFailureCount)

        // Simulate connectivity recovery
        cb.resetOnConnectivityRecovery()

        assertEquals(CircuitBreaker.State.CLOSED, cb.state)
        assertEquals(0, cb.consecutiveFailureCount)
        assertEquals(Instant.EPOCH, cb.nextRetryAt)
        assertTrue(cb.allowRequest())
    }

    @Test
    fun `circuit breaker success after failure resets state completely`() = runTest {
        val cb = CircuitBreaker(name = "test")

        cb.recordFailure()
        cb.recordFailure()
        assertEquals(2, cb.consecutiveFailureCount)

        cb.recordSuccess()
        assertEquals(0, cb.consecutiveFailureCount)
        assertEquals(Instant.EPOCH, cb.nextRetryAt)
        assertEquals(CircuitBreaker.State.CLOSED, cb.state)
    }

    // -------------------------------------------------------------------------
    // IngestionOrchestrator — dedup on re-fetch after crash
    // -------------------------------------------------------------------------

    @Test
    fun `re-fetched transactions after crash are deduped by bufferManager`() = runTest {
        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val bufferManager: TransactionBufferManager = mockk(relaxed = true)
        val adapter: IFccAdapter = mockk()

        coEvery { syncStateDao.get() } returns null // no prior cursor (simulating crash before persist)
        coEvery { syncStateDao.upsert(any()) } returns Unit

        val tx = makeCanonicalTransaction("FCC-REDUP")

        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = listOf(tx),
            hasMore = false,
            highWatermarkUtc = "2024-01-01T12:00:00Z",
        )

        // bufferTransaction returns false = already exists (dedup)
        coEvery { bufferManager.bufferTransaction(any()) } returns false

        val fccConfig = makeFccConfig()
        val orchestrator = IngestionOrchestrator(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
        ).also { it.wireRuntime(adapter, fccConfig) }

        val result = orchestrator.pollNow()

        assertEquals(0, result?.newCount)
        assertEquals(1, result?.skippedCount) // deduped
        assertTrue(result?.cursorAdvanced == true) // cursor still advances
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun makeTransaction(
        id: String = UUID.randomUUID().toString(),
        uploadAttempts: Int = 0,
    ): BufferedTransaction = BufferedTransaction(
        id = id,
        fccTransactionId = "FCC-${UUID.randomUUID()}",
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

    private fun makeCanonicalTransaction(fccTransactionId: String): CanonicalTransaction =
        CanonicalTransaction(
            id = UUID.randomUUID().toString(),
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
            fccVendor = FccVendor.DOMS,
            legalEntityId = "lei-1",
            status = TransactionStatus.PENDING,
            ingestionSource = IngestionSource.EDGE_UPLOAD,
            ingestedAt = "2024-01-01T10:01:05Z",
            updatedAt = "2024-01-01T10:01:05Z",
            schemaVersion = 1,
            isDuplicate = false,
            correlationId = UUID.randomUUID().toString(),
        )

    private fun makeFccConfig(): AgentFccConfig = AgentFccConfig(
        fccVendor = FccVendor.DOMS,
        connectionProtocol = "http",
        hostAddress = "192.168.1.100",
        port = 8080,
        authCredential = "secret",
        ingestionMode = IngestionMode.RELAY,
        pullIntervalSeconds = 30,
        productCodeMapping = emptyMap(),
        timezone = "Africa/Johannesburg",
        currencyCode = "ZAR",
    )
}
