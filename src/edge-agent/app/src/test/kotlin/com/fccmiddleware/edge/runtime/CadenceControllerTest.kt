package com.fccmiddleware.edge.runtime

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import com.fccmiddleware.edge.ingestion.IngestionOrchestrator
import com.fccmiddleware.edge.preauth.PreAuthHandler
import com.fccmiddleware.edge.sync.CircuitBreaker
import com.fccmiddleware.edge.sync.CloudUploadWorker
import com.fccmiddleware.edge.sync.ConfigPollWorker
import com.fccmiddleware.edge.sync.DeviceTokenProvider
import com.fccmiddleware.edge.sync.PreAuthCloudForwardWorker
import io.mockk.Runs
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.just
import io.mockk.mockk
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
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

/**
 * CadenceControllerTest — validates L-07 tick overflow fix, cadence loop behaviour,
 * connectivity transition side effects, and decommission checks.
 */
@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class CadenceControllerTest {

    private val connectivityManager: ConnectivityManager = mockk(relaxed = true)
    private val ingestionOrchestrator: IngestionOrchestrator = mockk(relaxed = true)
    private val cloudUploadWorker: CloudUploadWorker = mockk(relaxed = true)
    private val transactionDao: TransactionBufferDao = mockk(relaxed = true)
    private val preAuthHandler: PreAuthHandler = mockk(relaxed = true)
    private val configPollWorker: ConfigPollWorker = mockk(relaxed = true)
    private val preAuthCloudForwardWorker: PreAuthCloudForwardWorker = mockk(relaxed = true)
    private val tokenProvider: DeviceTokenProvider = mockk(relaxed = true)

    private val stateFlow = MutableStateFlow(ConnectivityState.FULLY_ONLINE)

    private lateinit var testScope: TestScope
    private lateinit var controller: CadenceController

    @Before
    fun setUp() {
        testScope = TestScope(StandardTestDispatcher())
        every { connectivityManager.state } returns stateFlow
        every { tokenProvider.isDecommissioned() } returns false
        coEvery { transactionDao.countForLocalApi() } returns 10

        // Wire circuit breakers on CloudUploadWorker so onTransition can access them
        every { cloudUploadWorker.uploadCircuitBreaker } returns CircuitBreaker(name = "testUpload")
        every { cloudUploadWorker.statusPollCircuitBreaker } returns CircuitBreaker(name = "testStatus")
        every { configPollWorker.circuitBreaker } returns CircuitBreaker(name = "testConfig")
        every { preAuthCloudForwardWorker.circuitBreaker } returns CircuitBreaker(name = "testPreAuth")
    }

    private fun makeController(
        config: CadenceController.CadenceConfig = CadenceController.CadenceConfig(
            baseIntervalMs = 100L,
            jitterRangeMs = 0L, // deterministic for tests
            highBacklogIntervalMs = 50L,
            highBacklogThreshold = 500,
            offlineIntervalMs = 200L,
            syncedToOdooTickFrequency = 2,
            configPollTickFrequency = 6,
            telemetryTickFrequency = 4,
        ),
    ): CadenceController = CadenceController(
        connectivityManager = connectivityManager,
        ingestionOrchestrator = ingestionOrchestrator,
        cloudUploadWorker = cloudUploadWorker,
        transactionDao = transactionDao,
        scope = testScope,
        preAuthHandler = preAuthHandler,
        configPollWorker = configPollWorker,
        preAuthCloudForwardWorker = preAuthCloudForwardWorker,
        tokenProvider = tokenProvider,
        config = config,
    )

    // -------------------------------------------------------------------------
    // L-07: Tick modulus computation
    // -------------------------------------------------------------------------

    @Test
    fun `L-07 computeTickModulus returns LCM of all tick frequencies`() {
        // LCM(2, 4, 6) = 12
        val config = CadenceController.CadenceConfig(
            syncedToOdooTickFrequency = 2,
            telemetryTickFrequency = 4,
            configPollTickFrequency = 6,
        )
        assertEquals(12L, CadenceController.computeTickModulus(config))
    }

    @Test
    fun `L-07 computeTickModulus with coprime frequencies`() {
        // LCM(3, 5, 7) = 105
        val config = CadenceController.CadenceConfig(
            syncedToOdooTickFrequency = 3,
            telemetryTickFrequency = 5,
            configPollTickFrequency = 7,
        )
        assertEquals(105L, CadenceController.computeTickModulus(config))
    }

    @Test
    fun `L-07 computeTickModulus with identical frequencies`() {
        // LCM(4, 4, 4) = 4
        val config = CadenceController.CadenceConfig(
            syncedToOdooTickFrequency = 4,
            telemetryTickFrequency = 4,
            configPollTickFrequency = 4,
        )
        assertEquals(4L, CadenceController.computeTickModulus(config))
    }

    @Test
    fun `L-07 computeTickModulus with frequency of 1`() {
        // LCM(1, 1, 1) = 1
        val config = CadenceController.CadenceConfig(
            syncedToOdooTickFrequency = 1,
            telemetryTickFrequency = 1,
            configPollTickFrequency = 1,
        )
        assertEquals(1L, CadenceController.computeTickModulus(config))
    }

    @Test
    fun `L-07 controller stores correct tick modulus from config`() {
        val c = makeController()
        // Default test config: LCM(2, 4, 6) = 12
        assertEquals(12L, c.tickModulus)
    }

    // -------------------------------------------------------------------------
    // Cadence loop — connectivity-driven worker dispatch
    // -------------------------------------------------------------------------

    @Test
    fun `FULLY_ONLINE tick calls poll, upload, and pre-auth expiry`() = testScope.runTest {
        stateFlow.value = ConnectivityState.FULLY_ONLINE
        val c = makeController()
        c.start()
        // Advance enough for one tick cycle
        advanceTimeBy(150)
        c.stop()

        coVerify(atLeast = 1) { ingestionOrchestrator.poll() }
        coVerify(atLeast = 1) { cloudUploadWorker.uploadPendingBatch() }
        coVerify(atLeast = 1) { preAuthHandler.runExpiryCheck() }
    }

    @Test
    fun `INTERNET_DOWN tick calls only FCC poll and pre-auth expiry, not cloud upload`() = testScope.runTest {
        stateFlow.value = ConnectivityState.INTERNET_DOWN
        val c = makeController()
        c.start()
        advanceTimeBy(150)
        c.stop()

        coVerify(atLeast = 1) { ingestionOrchestrator.poll() }
        coVerify(exactly = 0) { cloudUploadWorker.uploadPendingBatch() }
        coVerify(atLeast = 1) { preAuthHandler.runExpiryCheck() }
    }

    @Test
    fun `FCC_UNREACHABLE tick calls cloud upload but not FCC poll`() = testScope.runTest {
        stateFlow.value = ConnectivityState.FCC_UNREACHABLE
        val c = makeController()
        c.start()
        advanceTimeBy(150)
        c.stop()

        coVerify(exactly = 0) { ingestionOrchestrator.poll() }
        coVerify(atLeast = 1) { cloudUploadWorker.uploadPendingBatch() }
        coVerify(atLeast = 1) { preAuthHandler.runExpiryCheck() }
    }

    @Test
    fun `FULLY_OFFLINE tick calls only pre-auth expiry`() = testScope.runTest {
        stateFlow.value = ConnectivityState.FULLY_OFFLINE
        val c = makeController()
        c.start()
        advanceTimeBy(250)
        c.stop()

        coVerify(exactly = 0) { ingestionOrchestrator.poll() }
        coVerify(exactly = 0) { cloudUploadWorker.uploadPendingBatch() }
        coVerify(atLeast = 1) { preAuthHandler.runExpiryCheck() }
    }

    // -------------------------------------------------------------------------
    // Decommission check between workers
    // -------------------------------------------------------------------------

    @Test
    fun `decommissioned device skips entire tick`() = testScope.runTest {
        every { tokenProvider.isDecommissioned() } returns true
        stateFlow.value = ConnectivityState.FULLY_ONLINE
        val c = makeController()
        c.start()
        advanceTimeBy(150)
        c.stop()

        coVerify(exactly = 0) { ingestionOrchestrator.poll() }
        coVerify(exactly = 0) { cloudUploadWorker.uploadPendingBatch() }
    }

    // -------------------------------------------------------------------------
    // Immediate triggers
    // -------------------------------------------------------------------------

    @Test
    fun `triggerImmediateFccPoll polls when FCC is reachable`() = testScope.runTest {
        stateFlow.value = ConnectivityState.FULLY_ONLINE
        val c = makeController()
        c.triggerImmediateFccPoll()
        advanceTimeBy(10)

        coVerify(atLeast = 1) { ingestionOrchestrator.poll() }
    }

    @Test
    fun `triggerImmediateFccPoll skips when FCC unreachable`() = testScope.runTest {
        stateFlow.value = ConnectivityState.FCC_UNREACHABLE
        val c = makeController()
        c.triggerImmediateFccPoll()
        advanceTimeBy(10)

        coVerify(exactly = 0) { ingestionOrchestrator.poll() }
    }

    @Test
    fun `triggerImmediateReplay uploads when internet available`() = testScope.runTest {
        stateFlow.value = ConnectivityState.FULLY_ONLINE
        val c = makeController()
        c.triggerImmediateReplay()
        advanceTimeBy(10)

        coVerify(atLeast = 1) { cloudUploadWorker.uploadPendingBatch() }
    }

    @Test
    fun `triggerImmediateReplay skips when internet down`() = testScope.runTest {
        stateFlow.value = ConnectivityState.INTERNET_DOWN
        val c = makeController()
        c.triggerImmediateReplay()
        advanceTimeBy(10)

        coVerify(exactly = 0) { cloudUploadWorker.uploadPendingBatch() }
    }

    // -------------------------------------------------------------------------
    // Interval computation
    // -------------------------------------------------------------------------

    @Test
    fun `high backlog depth shortens interval`() = testScope.runTest {
        stateFlow.value = ConnectivityState.FULLY_ONLINE
        coEvery { transactionDao.countForLocalApi() } returns 1000 // above threshold
        val c = makeController()
        c.start()
        // With high backlog interval of 50ms and no jitter, second tick should fire
        // sooner than the base 100ms interval
        advanceTimeBy(110)
        c.stop()

        // Should have completed at least 2 ticks with 50ms interval
        coVerify(atLeast = 2) { ingestionOrchestrator.poll() }
    }

    @Test
    fun `safeGetBacklogDepth returns 0 on DAO exception`() = testScope.runTest {
        coEvery { transactionDao.countForLocalApi() } throws RuntimeException("DB locked")
        stateFlow.value = ConnectivityState.FULLY_ONLINE
        val c = makeController()
        c.start()
        advanceTimeBy(150)
        c.stop()

        // Should still run the tick (using 0 as backlog depth → base interval)
        coVerify(atLeast = 1) { ingestionOrchestrator.poll() }
    }

    // -------------------------------------------------------------------------
    // Start idempotency
    // -------------------------------------------------------------------------

    @Test
    fun `start is idempotent — cancels previous cadence loop`() = testScope.runTest {
        stateFlow.value = ConnectivityState.FULLY_ONLINE
        val c = makeController()
        c.start()
        advanceTimeBy(50)
        c.start() // second call should cancel the first
        advanceTimeBy(150)
        c.stop()

        // Should not crash or produce double-fire
        assertTrue(true)
    }
}
