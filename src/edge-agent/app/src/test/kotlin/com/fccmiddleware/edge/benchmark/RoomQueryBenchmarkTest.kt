package com.fccmiddleware.edge.benchmark

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * RoomQueryBenchmarkTest — validates Room query latency against a 30,000-record dataset.
 *
 * Pass/fail thresholds from performance guardrails §1:
 *   - getForLocalApi(50, 0):       p95 ≤ 150 ms
 *   - getPendingForUpload(50):     p95 ≤ 150 ms
 *   - countByStatus():             single call ≤ 100 ms
 *
 * Runs under Robolectric for local/CI execution without an Android device.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class RoomQueryBenchmarkTest {

    private lateinit var db: BufferDatabase
    private lateinit var dao: TransactionBufferDao

    @Before
    fun setUp() = runBlocking {
        db = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            BufferDatabase::class.java
        ).allowMainThreadQueries().build()
        dao = db.transactionDao()

        // Seed 30,000 records — must complete within 30 s
        val records = SeedDataGenerator.transactions(30_000)
        val seedStart = System.currentTimeMillis()
        records.forEach { dao.insert(it) }
        val seedMs = System.currentTimeMillis() - seedStart
        assertTrue("Seeding 30,000 records took ${seedMs}ms; must complete within 30,000ms", seedMs < 30_000)
    }

    @After
    fun tearDown() {
        db.close()
    }

    @Test
    fun `getForLocalApi p95 within 150ms on 30000 record dataset`() = runBlocking {
        val iterations = 50
        val latencies = LongArray(iterations)

        repeat(iterations) { i ->
            val start = System.currentTimeMillis()
            val results = dao.getForLocalApi(limit = 50, offset = 0)
            latencies[i] = System.currentTimeMillis() - start
            assertTrue("Expected 50 records, got ${results.size}", results.size == 50)
        }

        latencies.sort()
        val p95 = latencies[(iterations * 0.95).toInt() - 1]
        assertTrue("getForLocalApi p95=${p95}ms exceeds 150ms budget", p95 <= 150)
    }

    @Test
    fun `getPendingForUpload p95 within 150ms on 30000 record dataset`() = runBlocking {
        val iterations = 50
        val latencies = LongArray(iterations)

        repeat(iterations) { i ->
            val start = System.currentTimeMillis()
            val results = dao.getPendingForUpload(limit = 50)
            latencies[i] = System.currentTimeMillis() - start
            assertTrue("Expected results, got ${results.size}", results.isNotEmpty())
        }

        latencies.sort()
        val p95 = latencies[(iterations * 0.95).toInt() - 1]
        assertTrue("getPendingForUpload p95=${p95}ms exceeds 150ms budget", p95 <= 150)
    }

    @Test
    fun `countByStatus executes within 100ms`() = runBlocking {
        val start = System.currentTimeMillis()
        val counts = dao.countByStatus()
        val elapsed = System.currentTimeMillis() - start
        assertTrue("Expected status counts, got empty list", counts.isNotEmpty())
        assertTrue("countByStatus took ${elapsed}ms; should be < 100ms", elapsed < 100)
    }
}
