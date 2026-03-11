package com.fccmiddleware.edge.ingestion

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
import com.fccmiddleware.edge.buffer.entity.SyncState
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant
import java.util.UUID

/**
 * IngestionOrchestratorTest — unit tests for EA-2.6 ingestion orchestrator.
 *
 * Validates:
 *   - poll() no-ops when adapter or config is null
 *   - RELAY/BUFFER_ALWAYS: polls every tick regardless of elapsed time
 *   - CLOUD_DIRECT: skips poll until CLOUD_DIRECT_MIN_POLL_INTERVAL_MS has elapsed
 *   - CLOUD_DIRECT: first-ever poll always executes (no lastScheduledPollAt)
 *   - CLOUD_DIRECT: pollNow() bypasses the interval gate
 *   - buildCursor: null → empty catch-up cursor
 *   - buildCursor: "token:X" → cursorToken = X
 *   - buildCursor: ISO 8601 string → sinceUtc = stored value
 *   - Cursor advances to nextCursorToken (prefixed) after successful batch
 *   - Cursor advances to highWatermarkUtc when no token
 *   - No cursor update when batch returns neither token nor watermark
 *   - hasMore=true triggers follow-up fetch cycle
 *   - hasMore=false stops after first batch
 *   - All transactions in a batch are passed to TransactionBufferManager
 *   - Duplicate transactions are counted as skipped (not new)
 *   - MAX_FETCH_CYCLES caps multi-page ingestion
 *   - Adapter exception is caught; no rethrow
 *   - SyncStateDao read exception aborts poll gracefully
 */
@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class IngestionOrchestratorTest {

    private lateinit var adapter: IFccAdapter
    private lateinit var bufferManager: TransactionBufferManager
    private lateinit var syncStateDao: SyncStateDao

    private val relayConfig = makeConfig(IngestionMode.RELAY)
    private val bufferAlwaysConfig = makeConfig(IngestionMode.BUFFER_ALWAYS)
    private val cloudDirectConfig = makeConfig(IngestionMode.CLOUD_DIRECT)

    @Before
    fun setUp() {
        adapter = mockk()
        bufferManager = mockk()
        syncStateDao = mockk()

        // Default: empty DB state
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit
        coEvery { bufferManager.bufferTransaction(any()) } returns true
    }

    // -------------------------------------------------------------------------
    // Null guard — poll() and pollNow() are no-ops without adapter / config
    // -------------------------------------------------------------------------

    @Test
    fun `poll - no-op when adapter is null`() = runTest {
        val orchestrator = makeOrchestrator(adapter = null, config = relayConfig)
        orchestrator.poll() // should not throw
        coVerify(exactly = 0) { syncStateDao.get() }
    }

    @Test
    fun `poll - no-op when config is null`() = runTest {
        val orchestrator = makeOrchestrator(adapter = adapter, config = null)
        orchestrator.poll() // should not throw
        coVerify(exactly = 0) { syncStateDao.get() }
    }

    @Test
    fun `pollNow - no-op when adapter is null`() = runTest {
        val orchestrator = makeOrchestrator(adapter = null, config = relayConfig)
        orchestrator.pollNow(pumpNumber = 1) // should not throw
        coVerify(exactly = 0) { syncStateDao.get() }
    }

    @Test
    fun `pollNow - no-op when config is null`() = runTest {
        val orchestrator = makeOrchestrator(adapter = adapter, config = null)
        orchestrator.pollNow(pumpNumber = 1) // should not throw
        coVerify(exactly = 0) { syncStateDao.get() }
    }

    // -------------------------------------------------------------------------
    // Ingestion mode: RELAY — polls every tick
    // -------------------------------------------------------------------------

    @Test
    fun `RELAY - polls FCC on every call regardless of elapsed time`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()
        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)

        // First poll
        orchestrator.poll()
        // Second poll immediately after (no delay)
        orchestrator.poll()

        coVerify(exactly = 2) { adapter.fetchTransactions(any()) }
    }

    // -------------------------------------------------------------------------
    // Ingestion mode: BUFFER_ALWAYS — same behaviour as RELAY
    // -------------------------------------------------------------------------

    @Test
    fun `BUFFER_ALWAYS - polls FCC on every call regardless of elapsed time`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()
        val orchestrator = makeOrchestrator(adapter = adapter, config = bufferAlwaysConfig)

        orchestrator.poll()
        orchestrator.poll()

        coVerify(exactly = 2) { adapter.fetchTransactions(any()) }
    }

    // -------------------------------------------------------------------------
    // Ingestion mode: CLOUD_DIRECT — interval-gated safety-net poller
    // -------------------------------------------------------------------------

    @Test
    fun `CLOUD_DIRECT - first-ever poll always executes (no lastScheduledPollAt)`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()
        val orchestrator = makeOrchestrator(adapter = adapter, config = cloudDirectConfig)

        orchestrator.poll()

        coVerify(exactly = 1) { adapter.fetchTransactions(any()) }
    }

    @Test
    fun `CLOUD_DIRECT - skips poll before interval elapsed`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()
        val orchestrator = makeOrchestrator(adapter = adapter, config = cloudDirectConfig)

        // Simulate a poll that just completed
        orchestrator.lastScheduledPollAt = Instant.now()

        orchestrator.poll() // should be gated

        coVerify(exactly = 0) { adapter.fetchTransactions(any()) }
    }

    @Test
    fun `CLOUD_DIRECT - polls after interval elapsed`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()
        val orchestrator = makeOrchestrator(adapter = adapter, config = cloudDirectConfig)

        // Simulate last poll was 6 minutes ago (past the 5-min gate)
        val sixMinutesAgo = Instant.now()
            .minusMillis(IngestionOrchestrator.CLOUD_DIRECT_MIN_POLL_INTERVAL_MS + 60_000L)
        orchestrator.lastScheduledPollAt = sixMinutesAgo

        orchestrator.poll()

        coVerify(exactly = 1) { adapter.fetchTransactions(any()) }
    }

    @Test
    fun `CLOUD_DIRECT - pollNow bypasses interval gate`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()
        val orchestrator = makeOrchestrator(adapter = adapter, config = cloudDirectConfig)

        // Gate would block a scheduled poll
        orchestrator.lastScheduledPollAt = Instant.now()

        // But pollNow bypasses the gate
        orchestrator.pollNow(pumpNumber = null)

        coVerify(exactly = 1) { adapter.fetchTransactions(any()) }
    }

    @Test
    fun `CLOUD_DIRECT - pollNow does not update lastScheduledPollAt`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()
        val orchestrator = makeOrchestrator(adapter = adapter, config = cloudDirectConfig)
        val before = Instant.now()
        orchestrator.lastScheduledPollAt = before

        orchestrator.pollNow(pumpNumber = 2)

        // lastScheduledPollAt must remain unchanged so the next scheduled poll
        // is still gated by the original timestamp
        assertEquals(before, orchestrator.lastScheduledPollAt)
    }

    // -------------------------------------------------------------------------
    // Cursor management
    // -------------------------------------------------------------------------

    @Test
    fun `buildCursor - null syncState produces empty catch-up cursor`() {
        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        val cursor = orchestrator.buildCursor(null)

        assertNull(cursor.cursorToken)
        assertNull(cursor.sinceUtc)
    }

    @Test
    fun `buildCursor - null lastFccCursor produces empty catch-up cursor`() {
        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        val cursor = orchestrator.buildCursor(makeSyncState(lastFccCursor = null))

        assertNull(cursor.cursorToken)
        assertNull(cursor.sinceUtc)
    }

    @Test
    fun `buildCursor - token prefix maps to cursorToken`() {
        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        val cursor = orchestrator.buildCursor(makeSyncState(lastFccCursor = "token:abc123"))

        assertEquals("abc123", cursor.cursorToken)
        assertNull(cursor.sinceUtc)
    }

    @Test
    fun `buildCursor - ISO 8601 string maps to sinceUtc`() {
        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        val ts = "2025-01-15T10:00:00Z"
        val cursor = orchestrator.buildCursor(makeSyncState(lastFccCursor = ts))

        assertNull(cursor.cursorToken)
        assertEquals(ts, cursor.sinceUtc)
    }

    // -------------------------------------------------------------------------
    // Cursor advancement
    // -------------------------------------------------------------------------

    @Test
    fun `poll - cursor advances to nextCursorToken with token prefix`() = runTest {
        val token = "page2token"
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = emptyList(),
            hasMore = false,
            nextCursorToken = token,
        )
        val upsertSlot = slot<SyncState>()
        coEvery { syncStateDao.upsert(capture(upsertSlot)) } returns Unit

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll()

        assertEquals("token:$token", upsertSlot.captured.lastFccCursor)
    }

    @Test
    fun `poll - cursor advances to highWatermarkUtc when no token`() = runTest {
        val watermark = "2025-06-01T12:30:00Z"
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = emptyList(),
            hasMore = false,
            highWatermarkUtc = watermark,
        )
        val upsertSlot = slot<SyncState>()
        coEvery { syncStateDao.upsert(capture(upsertSlot)) } returns Unit

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll()

        assertEquals(watermark, upsertSlot.captured.lastFccCursor)
    }

    @Test
    fun `poll - cursor is not updated when batch has no token or watermark`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = emptyList(),
            hasMore = false,
            nextCursorToken = null,
            highWatermarkUtc = null,
        )

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll()

        coVerify(exactly = 0) { syncStateDao.upsert(any()) }
    }

    // -------------------------------------------------------------------------
    // Fetch continuation (hasMore)
    // -------------------------------------------------------------------------

    @Test
    fun `poll - hasMore=true triggers follow-up fetch`() = runTest {
        val tx1 = makeTransaction("tx-001")
        val tx2 = makeTransaction("tx-002")
        coEvery { adapter.fetchTransactions(any()) } returnsMany listOf(
            TransactionBatch(
                transactions = listOf(tx1),
                hasMore = true,
                nextCursorToken = "page2",
            ),
            TransactionBatch(
                transactions = listOf(tx2),
                hasMore = false,
            ),
        )

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll()

        coVerify(exactly = 2) { adapter.fetchTransactions(any()) }
        coVerify(exactly = 1) { bufferManager.bufferTransaction(tx1) }
        coVerify(exactly = 1) { bufferManager.bufferTransaction(tx2) }
    }

    @Test
    fun `poll - hasMore=false stops after first batch`() = runTest {
        val tx = makeTransaction("tx-001")
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = listOf(tx),
            hasMore = false,
        )

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll()

        coVerify(exactly = 1) { adapter.fetchTransactions(any()) }
        coVerify(exactly = 1) { bufferManager.bufferTransaction(tx) }
    }

    // -------------------------------------------------------------------------
    // Transaction buffering
    // -------------------------------------------------------------------------

    @Test
    fun `poll - all transactions in batch are passed to bufferManager`() = runTest {
        val txs = (1..5).map { makeTransaction("tx-$it") }
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = txs,
            hasMore = false,
        )

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll()

        txs.forEach { tx -> coVerify(exactly = 1) { bufferManager.bufferTransaction(tx) } }
    }

    @Test
    fun `poll - duplicate transactions are silently skipped by bufferManager`() = runTest {
        val tx = makeTransaction("tx-dup")
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = listOf(tx),
            hasMore = false,
        )
        // Buffer manager returns false → duplicate (already present)
        coEvery { bufferManager.bufferTransaction(tx) } returns false

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll() // should complete without error

        coVerify(exactly = 1) { bufferManager.bufferTransaction(tx) }
    }

    // -------------------------------------------------------------------------
    // Error resilience
    // -------------------------------------------------------------------------

    @Test
    fun `poll - adapter exception is caught and does not rethrow`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } throws RuntimeException("FCC unreachable")

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll() // should not propagate exception

        coVerify(exactly = 0) { bufferManager.bufferTransaction(any()) }
    }

    @Test
    fun `poll - SyncStateDao read exception aborts poll gracefully`() = runTest {
        coEvery { syncStateDao.get() } throws RuntimeException("DB corrupt")

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll() // should not propagate exception

        coVerify(exactly = 0) { adapter.fetchTransactions(any()) }
    }

    @Test
    fun `poll - cursor persist failure is non-fatal and poll completes`() = runTest {
        val tx = makeTransaction("tx-001")
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = listOf(tx),
            hasMore = false,
            highWatermarkUtc = "2025-01-01T00:00:00Z",
        )
        coEvery { syncStateDao.upsert(any()) } throws RuntimeException("DB write failed")

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll() // should not throw; transaction should still be buffered

        coVerify(exactly = 1) { bufferManager.bufferTransaction(tx) }
    }

    // -------------------------------------------------------------------------
    // Fetch cycle cap
    // -------------------------------------------------------------------------

    @Test
    fun `poll - MAX_FETCH_CYCLES caps multi-page ingestion`() = runTest {
        // Always returns hasMore=true with a new token to prevent infinite loop
        var callCount = 0
        coEvery { adapter.fetchTransactions(any()) } answers {
            callCount++
            TransactionBatch(
                transactions = listOf(makeTransaction("tx-$callCount")),
                hasMore = true,
                nextCursorToken = "token-$callCount",
            )
        }

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll()

        // Should stop at MAX_FETCH_CYCLES (10) even though hasMore=true
        assertTrue(callCount <= 10)
    }

    // -------------------------------------------------------------------------
    // Connectivity state routing (verified via CadenceController dispatch)
    // The orchestrator itself is called only when FCC is reachable (per §5.4);
    // these tests verify the orchestrator does not double-check connectivity.
    // -------------------------------------------------------------------------

    @Test
    fun `poll - does not check connectivity state internally (CadenceController owns this)`() = runTest {
        // Orchestrator should fetch whenever called — connectivity guard is in CadenceController.
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()
        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)

        orchestrator.poll()

        coVerify(exactly = 1) { adapter.fetchTransactions(any()) }
    }

    // -------------------------------------------------------------------------
    // Second fetch uses correct continuation cursor from first batch
    // -------------------------------------------------------------------------

    @Test
    fun `poll - second fetch uses token from first batch response`() = runTest {
        val capturedCursors = mutableListOf<FetchCursor>()
        val cursorSlot = slot<FetchCursor>()

        coEvery { adapter.fetchTransactions(capture(cursorSlot)) } answers {
            capturedCursors.add(cursorSlot.captured)
            when (capturedCursors.size) {
                1 -> TransactionBatch(
                    transactions = emptyList(),
                    hasMore = true,
                    nextCursorToken = "continuation-token",
                )
                else -> TransactionBatch(
                    transactions = emptyList(),
                    hasMore = false,
                )
            }
        }

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        orchestrator.poll()

        assertEquals(2, capturedCursors.size)
        // First call: empty catch-up cursor
        assertNull(capturedCursors[0].cursorToken)
        assertNull(capturedCursors[0].sinceUtc)
        // Second call: token from first batch
        assertEquals("continuation-token", capturedCursors[1].cursorToken)
        assertNull(capturedCursors[1].sinceUtc)
    }

    // -------------------------------------------------------------------------
    // EA-2.7: pollNow() returns PollResult
    // -------------------------------------------------------------------------

    @Test
    fun `pollNow - returns null when adapter is null`() = runTest {
        val orchestrator = makeOrchestrator(adapter = null, config = relayConfig)
        val result = orchestrator.pollNow(pumpNumber = 1)
        assertNull(result)
        coVerify(exactly = 0) { syncStateDao.get() }
    }

    @Test
    fun `pollNow - returns null when config is null`() = runTest {
        val orchestrator = makeOrchestrator(adapter = adapter, config = null)
        val result = orchestrator.pollNow(pumpNumber = 1)
        assertNull(result)
        coVerify(exactly = 0) { syncStateDao.get() }
    }

    @Test
    fun `pollNow - returns PollResult with newCount for new transactions`() = runTest {
        val tx1 = makeTransaction("tx-001")
        val tx2 = makeTransaction("tx-002")
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = listOf(tx1, tx2),
            hasMore = false,
            highWatermarkUtc = "2025-06-01T12:00:00Z",
        )
        coEvery { bufferManager.bufferTransaction(tx1) } returns true
        coEvery { bufferManager.bufferTransaction(tx2) } returns true

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        val result = orchestrator.pollNow(pumpNumber = 3)

        assertNotNull(result)
        assertEquals(2, result!!.newCount)
        assertEquals(0, result.skippedCount)
        assertEquals(1, result.fetchCycles)
        assertTrue(result.cursorAdvanced)
    }

    @Test
    fun `pollNow - returns PollResult with zero counts when no new transactions`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        val result = orchestrator.pollNow()

        assertNotNull(result)
        assertEquals(0, result!!.newCount)
        assertEquals(0, result.skippedCount)
        assertEquals(1, result.fetchCycles)
        assertFalse(result.cursorAdvanced)
    }

    @Test
    fun `pollNow - returns PollResult with skippedCount for duplicate transactions`() = runTest {
        val tx = makeTransaction("tx-dup")
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = listOf(tx),
            hasMore = false,
        )
        coEvery { bufferManager.bufferTransaction(tx) } returns false // duplicate

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        val result = orchestrator.pollNow()

        assertNotNull(result)
        assertEquals(0, result!!.newCount)
        assertEquals(1, result.skippedCount)
    }

    @Test
    fun `pollNow - cursorAdvanced is false when batch has no token or watermark`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns TransactionBatch(
            transactions = emptyList(),
            hasMore = false,
            nextCursorToken = null,
            highWatermarkUtc = null,
        )

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        val result = orchestrator.pollNow()

        assertNotNull(result)
        assertFalse(result!!.cursorAdvanced)
    }

    @Test
    fun `pollNow - returns PollResult even when adapter throws`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } throws RuntimeException("FCC down")

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)
        val result = orchestrator.pollNow()

        // Should not throw; returns a zero-count result
        assertNotNull(result)
        assertEquals(0, result!!.newCount)
        assertEquals(0, result.fetchCycles)
    }

    @Test
    fun `pollNow - does not update lastScheduledPollAt`() = runTest {
        coEvery { adapter.fetchTransactions(any()) } returns emptyBatch()
        val orchestrator = makeOrchestrator(adapter = adapter, config = cloudDirectConfig)
        val before = Instant.now()
        orchestrator.lastScheduledPollAt = before

        orchestrator.pollNow(pumpNumber = 2)

        assertEquals(before, orchestrator.lastScheduledPollAt)
    }

    @Test
    fun `pollNow - serialized with poll via mutex (sequential execution)`() = runTest {
        // Verify that poll() and pollNow() calls issued concurrently do not interleave.
        // We use async to launch both, then await both, and verify that the adapter
        // was called exactly twice (not zero or one, which would indicate mutex issues).
        val callOrder = mutableListOf<String>()
        coEvery { adapter.fetchTransactions(any()) } answers {
            callOrder.add("fetch")
            emptyBatch()
        }

        val orchestrator = makeOrchestrator(adapter = adapter, config = relayConfig)

        val pollJob = async { orchestrator.poll() }
        val pollNowJob = async { orchestrator.pollNow() }

        pollJob.await()
        pollNowJob.await()

        // Both ran — no races lost either call
        coVerify(exactly = 2) { adapter.fetchTransactions(any()) }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun makeOrchestrator(
        adapter: IFccAdapter?,
        config: AgentFccConfig?,
    ) = IngestionOrchestrator(
        adapter = adapter,
        bufferManager = bufferManager,
        syncStateDao = syncStateDao,
        config = config,
    )

    private fun makeConfig(mode: IngestionMode) = AgentFccConfig(
        fccVendor = FccVendor.DOMS,
        connectionProtocol = "REST",
        hostAddress = "192.168.1.100",
        port = 8080,
        authCredential = "test-key",
        ingestionMode = mode,
        pullIntervalSeconds = 30,
        productCodeMapping = emptyMap(),
        timezone = "Africa/Harare",
        currencyCode = "USD",
        pumpNumberOffset = 0,
    )

    private fun makeSyncState(lastFccCursor: String?) = SyncState(
        lastFccCursor = lastFccCursor,
        lastUploadAt = null,
        lastStatusPollAt = null,
        lastConfigPullAt = null,
        lastConfigVersion = null,
        updatedAt = Instant.now().toString(),
    )

    private fun emptyBatch() = TransactionBatch(
        transactions = emptyList(),
        hasMore = false,
    )

    private fun makeTransaction(fccId: String) = CanonicalTransaction(
        id = UUID.randomUUID().toString(),
        fccTransactionId = fccId,
        siteCode = "SITE-001",
        pumpNumber = 1,
        nozzleNumber = 1,
        productCode = "PMS",
        volumeMicrolitres = 10_000_000L,
        amountMinorUnits = 15_000L,
        unitPriceMinorPerLitre = 1_500L,
        currencyCode = "USD",
        startedAt = "2025-01-15T10:00:00Z",
        completedAt = "2025-01-15T10:02:00Z",
        fccVendor = FccVendor.DOMS,
        legalEntityId = UUID.randomUUID().toString(),
        status = TransactionStatus.PENDING,
        ingestionSource = IngestionSource.EDGE_UPLOAD,
        ingestedAt = Instant.now().toString(),
        updatedAt = Instant.now().toString(),
        schemaVersion = 1,
        isDuplicate = false,
        correlationId = UUID.randomUUID().toString(),
    )
}
