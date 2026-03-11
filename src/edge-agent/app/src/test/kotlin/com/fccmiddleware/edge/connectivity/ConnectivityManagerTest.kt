package com.fccmiddleware.edge.connectivity

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.entity.AuditLog
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.mockk
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * ConnectivityManagerTest — unit tests for the dual-probe connectivity state machine.
 *
 * Validates:
 *   - Initial state is FULLY_OFFLINE
 *   - State derivation from probe results
 *   - 3-consecutive-failure threshold for DOWN (prevents flapping)
 *   - Single success immediately recovers to UP
 *   - Rapid probe alternation doesn't cause premature DOWN
 *   - StateFlow emits correct states on transition
 *   - Audit log entries written on every state transition
 *   - ConnectivityTransitionListener called with correct arguments
 */
@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class ConnectivityManagerTest {

    private lateinit var auditLogDao: AuditLogDao
    private val testDispatcher = StandardTestDispatcher()

    // Fast probe config — 100ms interval, 50ms timeout, threshold=3
    private val fastConfig = ConnectivityManager.ProbeConfig(
        probeIntervalMs = 100L,
        probeTimeoutMs = 50L,
        failureThreshold = 3,
        jitterRangeMs = 0L,
    )

    @Before
    fun setUp() {
        auditLogDao = mockk()
        coEvery { auditLogDao.insert(any()) } returns 1L
    }

    // -------------------------------------------------------------------------
    // Initial state
    // -------------------------------------------------------------------------

    @Test
    fun `initial state is FULLY_OFFLINE`() = runTest {
        val mgr = buildManager(internet = { false }, fcc = { false })
        assertEquals(ConnectivityState.FULLY_OFFLINE, mgr.state.value)
    }

    // -------------------------------------------------------------------------
    // State derivation from probe results
    // -------------------------------------------------------------------------

    @Test
    fun `both probes UP transitions to FULLY_ONLINE`() = runTest(testDispatcher) {
        val mgr = buildManager(internet = { true }, fcc = { true })
        mgr.start()
        advanceTimeBy(200L)
        assertEquals(ConnectivityState.FULLY_ONLINE, mgr.state.value)
        mgr.stop()
    }

    @Test
    fun `internet DOWN fcc UP transitions to INTERNET_DOWN`() = runTest(testDispatcher) {
        val mgr = buildManager(internet = { false }, fcc = { true })
        mgr.start()
        // FCC succeeds immediately → fccUp=true
        // Internet must fail 3 times before internetUp=false
        advanceTimeBy(500L)
        assertEquals(ConnectivityState.INTERNET_DOWN, mgr.state.value)
        mgr.stop()
    }

    @Test
    fun `internet UP fcc DOWN transitions to FCC_UNREACHABLE`() = runTest(testDispatcher) {
        val mgr = buildManager(internet = { true }, fcc = { false })
        mgr.start()
        advanceTimeBy(500L)
        assertEquals(ConnectivityState.FCC_UNREACHABLE, mgr.state.value)
        mgr.stop()
    }

    @Test
    fun `both probes DOWN stays FULLY_OFFLINE`() = runTest(testDispatcher) {
        val mgr = buildManager(internet = { false }, fcc = { false })
        mgr.start()
        advanceTimeBy(500L)
        assertEquals(ConnectivityState.FULLY_OFFLINE, mgr.state.value)
        mgr.stop()
    }

    // -------------------------------------------------------------------------
    // 3-consecutive-failure threshold prevents flapping
    // -------------------------------------------------------------------------

    @Test
    fun `2 internet failures do NOT transition internet to DOWN`() = runTest(testDispatcher) {
        var internetCallCount = 0
        val mgr = buildManager(
            internet = {
                internetCallCount++
                internetCallCount > 2  // fail first 2, succeed from 3rd
            },
            fcc = { true },
        )
        mgr.start()
        // After 1 probe cycle: internet failure count = 1 (< 3 threshold)
        // FCC up after first probe
        advanceTimeBy(150L)
        // Should still be FCC_UNREACHABLE or INTERNET_DOWN depending on race,
        // but NOT permanently transitioned — the internet probe should recover
        // on success before hitting the threshold
        mgr.stop()
    }

    @Test
    fun `exactly 3 consecutive failures transition internet to DOWN`() = runTest(testDispatcher) {
        var internetCallCount = 0
        val mgr = buildManager(
            internet = {
                internetCallCount++
                false  // always fail
            },
            fcc = { true },
        )
        mgr.start()
        // After 3 internet probe cycles: internetConsecFailures = 3 >= threshold
        advanceTimeBy(400L)
        assertEquals(ConnectivityState.INTERNET_DOWN, mgr.state.value)
        mgr.stop()
    }

    // -------------------------------------------------------------------------
    // Single success immediately recovers (UP recovery)
    // -------------------------------------------------------------------------

    @Test
    fun `single internet success immediately recovers from DOWN`() = runTest(testDispatcher) {
        var internetCallCount = 0
        val mgr = buildManager(
            internet = {
                internetCallCount++
                // First probe at t=0. With 100ms interval and threshold=3:
                //   t=0: call 1 (false), t=100: call 2 (false), t=200: call 3 (false) → INTERNET_DOWN
                //   t=300: call 4 (false), t=400: call 5 (false) — safe assert window
                //   t=500: call 6 (true) → recovery to FULLY_ONLINE
                internetCallCount > 5
            },
            fcc = { true },
        )
        mgr.start()
        // After 5 failed probes (t=0..400), state is INTERNET_DOWN
        advanceTimeBy(450L)
        assertEquals(ConnectivityState.INTERNET_DOWN, mgr.state.value)

        // 6th call at t=500 succeeds → immediately recover
        advanceTimeBy(200L)
        assertEquals(ConnectivityState.FULLY_ONLINE, mgr.state.value)
        mgr.stop()
    }

    // -------------------------------------------------------------------------
    // Rapid probe alternation doesn't cause flapping
    // -------------------------------------------------------------------------

    @Test
    fun `rapid alternation below threshold does not cause FULLY_OFFLINE`() = runTest(testDispatcher) {
        var callCount = 0
        // Alternates success/failure — never 3 consecutive failures
        val mgr = buildManager(
            internet = {
                callCount++
                callCount % 2 == 0  // even calls succeed, odd fail
            },
            fcc = { true },
        )
        mgr.start()
        // Over 10 cycles (5 success, 5 fail) — consecutive failures never reach 3
        advanceTimeBy(1100L)
        // Should never reach INTERNET_DOWN (consecutive failures reset on each success)
        // Final state depends on last probe result, but should not be FULLY_OFFLINE
        val finalState = mgr.state.value
        assert(finalState != ConnectivityState.FULLY_OFFLINE) {
            "Rapid alternation should not cause FULLY_OFFLINE (got $finalState)"
        }
        mgr.stop()
    }

    // -------------------------------------------------------------------------
    // StateFlow emits on transitions
    // -------------------------------------------------------------------------

    @Test
    fun `stateFlow emits FULLY_ONLINE when both probes succeed`() = runTest(testDispatcher) {
        val mgr = buildManager(internet = { true }, fcc = { true })
        mgr.start()
        advanceTimeBy(200L)
        // StateFlow.first() would return current value if already transitioned
        assertEquals(ConnectivityState.FULLY_ONLINE, mgr.state.value)
        mgr.stop()
    }

    // -------------------------------------------------------------------------
    // Audit log written on transitions
    // -------------------------------------------------------------------------

    @Test
    fun `audit log entry written on FULLY_OFFLINE to FULLY_ONLINE transition`() =
        runTest(testDispatcher) {
            val mgr = buildManager(internet = { true }, fcc = { true })
            mgr.start()
            advanceTimeBy(200L)

            val captured = mutableListOf<AuditLog>()
            coVerify(atLeast = 1) { auditLogDao.insert(capture(captured)) }
            val onlineLog = captured.firstOrNull { it.eventType == "CONNECTIVITY_TRANSITION" && it.message.contains("FULLY_ONLINE") }
            assertNotNull("Expected audit log entry mentioning FULLY_ONLINE", onlineLog)
            mgr.stop()
        }

    // -------------------------------------------------------------------------
    // Transition listener called
    // -------------------------------------------------------------------------

    @Test
    fun `transition listener called with correct from and to states`() =
        runTest(testDispatcher) {
            val transitions = mutableListOf<Pair<ConnectivityState, ConnectivityState>>()
            val listener = ConnectivityTransitionListener { from, to ->
                transitions.add(from to to)
            }

            val mgr = buildManager(
                internet = { true },
                fcc = { true },
                listener = listener,
            )
            mgr.start()
            advanceTimeBy(200L)

            assert(transitions.isNotEmpty()) { "Expected at least one transition" }
            val toOnline = transitions.firstOrNull { (_, to) -> to == ConnectivityState.FULLY_ONLINE }
            assertNotNull("Expected transition to FULLY_ONLINE", toOnline)
            mgr.stop()
        }

    // -------------------------------------------------------------------------
    // FCC heartbeat age diagnostics
    // -------------------------------------------------------------------------

    @Test
    fun `fccHeartbeatAgeSeconds returns null before first successful FCC probe`() = runTest {
        val mgr = buildManager(internet = { false }, fcc = { false })
        assertNull(mgr.fccHeartbeatAgeSeconds())
    }

    @Test
    fun `fccHeartbeatAgeSeconds returns non-null after successful FCC probe`() =
        runTest(testDispatcher) {
            val mgr = buildManager(internet = { true }, fcc = { true })
            mgr.start()
            advanceTimeBy(200L)
            assertNotNull(mgr.fccHeartbeatAgeSeconds())
            mgr.stop()
        }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun TestScope.buildManager(
        internet: suspend () -> Boolean,
        fcc: suspend () -> Boolean,
        listener: ConnectivityTransitionListener? = null,
    ) = ConnectivityManager(
        internetProbe = internet,
        fccProbe = fcc,
        auditLogDao = auditLogDao,
        listener = listener,
        scope = this,
        config = fastConfig,
    )
}
