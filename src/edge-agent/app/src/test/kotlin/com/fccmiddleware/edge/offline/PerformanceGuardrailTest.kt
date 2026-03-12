package com.fccmiddleware.edge.offline

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.offline.OfflineScenarioTestHelpers.makeTransaction
import com.fccmiddleware.edge.sync.CloudApiClient
import com.fccmiddleware.edge.sync.CloudUploadRecordResult
import com.fccmiddleware.edge.sync.CloudUploadRequest
import com.fccmiddleware.edge.sync.CloudUploadResponse
import com.fccmiddleware.edge.sync.CloudUploadResult
import com.fccmiddleware.edge.sync.CloudUploadWorker
import com.fccmiddleware.edge.sync.CloudUploadWorkerConfig
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import io.mockk.coEvery
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID

/**
 * PerformanceGuardrailTest — EA-6.1 performance guardrail validation.
 *
 * Measures actual performance against the documented thresholds from
 * `docs/plans/dev-plan-edge-agent.md`:
 *
 * - GET /api/transactions p95 with 30K records: <= 150 ms
 * - Replay throughput: >= 600 tx/min
 * - Steady-state RSS: <= 180 MB
 * - 30K buffer without OOM or query degradation
 *
 * Uses Room in-memory DB with Robolectric for real SQLite queries.
 * Battery drain tests require physical hardware — documented as manual test procedures.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class PerformanceGuardrailTest {

    private lateinit var db: BufferDatabase

    @Before
    fun setUp() {
        db = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            BufferDatabase::class.java,
        )
            .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
            .allowMainThreadQueries()
            .build()
    }

    @After
    fun tearDown() {
        db.close()
    }

    // -------------------------------------------------------------------------
    // GET /api/transactions query latency with 30K records
    // Guardrail: p95 <= 150ms for limit <= 50
    // -------------------------------------------------------------------------

    @Test
    fun `local API query p95 latency under 150ms with 30000 records`() = runBlocking {
        val dao = db.transactionDao()
        val baseTime = Instant.now()
        val windowSeconds = ChronoUnit.DAYS.getDuration().seconds * 30

        // Seed 30,000 records with representative distribution
        for (i in 0 until 30_000) {
            val syncStatus = when {
                i % 10 == 9 -> "SYNCED_TO_ODOO"
                i % 10 >= 7 -> "UPLOADED"
                else -> "PENDING"
            }
            dao.insert(makeTransaction(
                index = i,
                baseTime = baseTime,
                windowSeconds = windowSeconds,
                syncStatus = syncStatus,
            ))
        }

        // Run 20 queries and measure latency
        val latencies = mutableListOf<Long>()
        for (run in 0 until 20) {
            val startNs = System.nanoTime()
            dao.getForLocalApi(limit = 50, offset = 0)
            val elapsedMs = (System.nanoTime() - startNs) / 1_000_000
            latencies.add(elapsedMs)
        }

        latencies.sort()
        val p95Index = (latencies.size * 0.95).toInt().coerceAtMost(latencies.size - 1)
        val p95Ms = latencies[p95Index]

        assertTrue(
            "Local API query p95: ${p95Ms}ms; must be <= 150ms with 30K records",
            p95Ms <= 150,
        )
    }

    @Test
    fun `pump-filtered query latency under 150ms with 30000 records`() = runBlocking {
        val dao = db.transactionDao()
        val baseTime = Instant.now()
        val windowSeconds = ChronoUnit.DAYS.getDuration().seconds * 30

        for (i in 0 until 30_000) {
            val syncStatus = when {
                i % 10 == 9 -> "SYNCED_TO_ODOO"
                i % 10 >= 7 -> "UPLOADED"
                else -> "PENDING"
            }
            dao.insert(makeTransaction(
                index = i,
                baseTime = baseTime,
                windowSeconds = windowSeconds,
                syncStatus = syncStatus,
            ))
        }

        val latencies = mutableListOf<Long>()
        for (run in 0 until 20) {
            val startNs = System.nanoTime()
            dao.getForLocalApiByPump(pumpNumber = 1, limit = 50, offset = 0)
            val elapsedMs = (System.nanoTime() - startNs) / 1_000_000
            latencies.add(elapsedMs)
        }

        latencies.sort()
        val p95Index = (latencies.size * 0.95).toInt().coerceAtMost(latencies.size - 1)
        val p95Ms = latencies[p95Index]

        assertTrue(
            "Pump-filtered query p95: ${p95Ms}ms; must be <= 150ms",
            p95Ms <= 150,
        )
    }

    // -------------------------------------------------------------------------
    // getPendingForUpload query latency with 30K records
    // -------------------------------------------------------------------------

    @Test
    fun `upload batch query latency under 150ms with 30000 records`() = runBlocking {
        val dao = db.transactionDao()
        val baseTime = Instant.now()
        val windowSeconds = ChronoUnit.DAYS.getDuration().seconds * 30

        for (i in 0 until 30_000) {
            dao.insert(makeTransaction(
                index = i,
                baseTime = baseTime,
                windowSeconds = windowSeconds,
            ))
        }

        val latencies = mutableListOf<Long>()
        for (run in 0 until 20) {
            val startNs = System.nanoTime()
            dao.getPendingForUpload(limit = 50)
            val elapsedMs = (System.nanoTime() - startNs) / 1_000_000
            latencies.add(elapsedMs)
        }

        latencies.sort()
        val p95Index = (latencies.size * 0.95).toInt().coerceAtMost(latencies.size - 1)
        val p95Ms = latencies[p95Index]

        assertTrue(
            "Upload batch query p95: ${p95Ms}ms; must be <= 150ms with 30K records",
            p95Ms <= 150,
        )
    }

    // -------------------------------------------------------------------------
    // Replay throughput: >= 600 tx/min
    // -------------------------------------------------------------------------

    @Test
    fun `replay throughput with mock cloud meets 600 tx per minute`() = runTest {
        val tokenProvider: DeviceTokenProvider = mockk()
        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns "valid-jwt"
        every { tokenProvider.getLegalEntityId() } returns "10000000-0000-0000-0000-000000000001"

        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val bufferManager: TransactionBufferManager = mockk(relaxed = true)
        val cloudApiClient: CloudApiClient = mockk()

        val config = CloudUploadWorkerConfig(
            uploadBatchSize = 50,
            baseBackoffMs = 1_000L,
            maxBackoffMs = 60_000L,
        )

        val worker = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            config = config,
        )

        val totalRecords = 1_000
        val allRecords = (0 until totalRecords).map { makeTransaction(index = it) }
        val batchSize = config.uploadBatchSize
        val totalBatches = (totalRecords + batchSize - 1) / batchSize

        var batchIndex = 0
        coEvery { bufferManager.getPendingBatch(any()) } answers {
            val start = batchIndex * batchSize
            val end = minOf(start + batchSize, allRecords.size)
            if (start >= allRecords.size) emptyList()
            else allRecords.subList(start, end)
        }

        coEvery { cloudApiClient.uploadBatch(any(), any()) } answers {
            val req = firstArg<CloudUploadRequest>()
            CloudUploadResult.Success(
                CloudUploadResponse(
                    results = req.transactions.map {
                        CloudUploadRecordResult(
                            fccTransactionId = it.fccTransactionId,
                            siteCode = it.siteCode,
                            outcome = "ACCEPTED",
                            id = UUID.randomUUID().toString(),
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
            worker.uploadPendingBatch()
        }
        val elapsedMs = System.currentTimeMillis() - startMs
        val txPerMinute = if (elapsedMs > 0) (totalRecords.toLong() * 60_000L) / elapsedMs else Long.MAX_VALUE

        assertTrue(
            "Replay throughput: $txPerMinute tx/min (${elapsedMs}ms for $totalRecords); must be >= 600",
            txPerMinute >= 600,
        )
    }

    // -------------------------------------------------------------------------
    // Memory — 30K record buffer without OOM
    // Guardrail: steady-state RSS <= 180 MB
    // -------------------------------------------------------------------------

    @Test
    fun `30000 record buffer does not exceed memory guardrail`() = runBlocking {
        val dao = db.transactionDao()
        val runtime = Runtime.getRuntime()

        // Force GC before measurement
        System.gc()
        Thread.sleep(100)
        val baselineUsed = runtime.totalMemory() - runtime.freeMemory()

        // Insert 30,000 records
        val baseTime = Instant.now()
        val windowSeconds = ChronoUnit.DAYS.getDuration().seconds * 30
        for (i in 0 until 30_000) {
            dao.insert(makeTransaction(
                index = i,
                baseTime = baseTime,
                windowSeconds = windowSeconds,
            ))
        }

        // Force GC after insertion
        System.gc()
        Thread.sleep(100)
        val afterUsed = runtime.totalMemory() - runtime.freeMemory()
        val deltaBytes = afterUsed - baselineUsed
        val deltaMb = deltaBytes / (1024 * 1024)

        // The in-process delta should be well under 180 MB (the RSS target includes
        // Android framework overhead; raw data + indexes for 30K records should be
        // under 50 MB even with raw payload JSON).
        assertTrue(
            "Memory delta for 30K records: ${deltaMb}MB; JVM heap increment should be < 180MB",
            deltaMb < 180,
        )

        // Verify data integrity after bulk insert
        val count = dao.countForLocalApi()
        assertTrue("All 30K records should be queryable", count >= 27_000)  // excluding SYNCED_TO_ODOO
    }

    // -------------------------------------------------------------------------
    // Buffer count telemetry with 30K records
    // -------------------------------------------------------------------------

    @Test
    fun `countByStatus performs efficiently with 30000 records`() = runBlocking {
        val dao = db.transactionDao()
        val baseTime = Instant.now()
        val windowSeconds = ChronoUnit.DAYS.getDuration().seconds * 30

        for (i in 0 until 30_000) {
            val syncStatus = when {
                i % 10 == 9 -> "SYNCED_TO_ODOO"
                i % 10 >= 7 -> "UPLOADED"
                else -> "PENDING"
            }
            dao.insert(makeTransaction(
                index = i,
                baseTime = baseTime,
                windowSeconds = windowSeconds,
                syncStatus = syncStatus,
            ))
        }

        val latencies = mutableListOf<Long>()
        for (run in 0 until 20) {
            val startNs = System.nanoTime()
            dao.countByStatus()
            val elapsedMs = (System.nanoTime() - startNs) / 1_000_000
            latencies.add(elapsedMs)
        }

        latencies.sort()
        val p95Index = (latencies.size * 0.95).toInt().coerceAtMost(latencies.size - 1)
        val p95Ms = latencies[p95Index]

        // countByStatus should be fast — GROUP BY on indexed column
        assertTrue(
            "countByStatus p95: ${p95Ms}ms; should be < 200ms",
            p95Ms < 200,
        )
    }

    // -------------------------------------------------------------------------
    // Oldest pending query for sync lag
    // -------------------------------------------------------------------------

    @Test
    fun `oldestPendingCreatedAt fast with 30000 records`() = runBlocking {
        val dao = db.transactionDao()
        val baseTime = Instant.now()
        val windowSeconds = ChronoUnit.DAYS.getDuration().seconds * 30

        for (i in 0 until 30_000) {
            dao.insert(makeTransaction(
                index = i,
                baseTime = baseTime,
                windowSeconds = windowSeconds,
            ))
        }

        val latencies = mutableListOf<Long>()
        for (run in 0 until 20) {
            val startNs = System.nanoTime()
            dao.oldestPendingCreatedAt()
            val elapsedMs = (System.nanoTime() - startNs) / 1_000_000
            latencies.add(elapsedMs)
        }

        latencies.sort()
        val p95Index = (latencies.size * 0.95).toInt().coerceAtMost(latencies.size - 1)
        val p95Ms = latencies[p95Index]

        assertTrue(
            "oldestPendingCreatedAt p95: ${p95Ms}ms; should be < 100ms",
            p95Ms < 100,
        )
    }

    // -------------------------------------------------------------------------
    // Backoff calculation correctness under stress
    // -------------------------------------------------------------------------

    @Test
    fun `exponential backoff caps correctly under prolonged failure`() = runTest {
        val config = CloudUploadWorkerConfig(
            uploadBatchSize = 50,
            baseBackoffMs = 1_000L,
            maxBackoffMs = 60_000L,
        )
        val worker = CloudUploadWorker(config = config)

        // Simulate 100 consecutive failures via circuit breaker
        val cb = worker.uploadCircuitBreaker
        val backoffs = (1..100).map { cb.recordFailure() }

        // All must be >= 0 and <= maxBackoffMs
        assertTrue(
            "All backoff values must be >= 0",
            backoffs.all { it >= 0 },
        )
        assertTrue(
            "All backoff values must be <= maxBackoffMs (${config.maxBackoffMs})",
            backoffs.all { it <= config.maxBackoffMs },
        )

        // First failure = 1s
        assertTrue("First failure backoff should be 1000ms", backoffs[0] == 1_000L)
        // Should reach cap by failure 7 (64s > 60s cap)
        assertTrue("Backoff should cap at 60s", backoffs[6] == 60_000L)
        // All subsequent failures at cap
        assertTrue(
            "All failures after cap should remain at max",
            backoffs.drop(6).all { it == 60_000L },
        )
    }
}
