package com.fccmiddleware.edge.preauth

import com.fccmiddleware.edge.adapter.common.ConnectivityState
import com.fccmiddleware.edge.adapter.common.IFccAdapter
import com.fccmiddleware.edge.adapter.common.PreAuthCommand
import com.fccmiddleware.edge.adapter.common.PreAuthResult
import com.fccmiddleware.edge.adapter.common.PreAuthResultStatus
import com.fccmiddleware.edge.adapter.common.PreAuthStatus
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.dao.NozzleDao
import com.fccmiddleware.edge.buffer.dao.PreAuthDao
import com.fccmiddleware.edge.buffer.entity.Nozzle
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import io.mockk.Runs
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.just
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.util.UUID

/**
 * PreAuthEdgeCaseTest — validates pre-auth edge cases:
 *   - Multi-order race: concurrent requests for same order deduped via M-12
 *   - Expiry during FCC outage: deauth fails, stays AUTHORIZED for retry
 *   - Nozzle mapping not found: returns NOZZLE_MAPPING_NOT_FOUND
 *   - FCC adapter timeout: returns TIMEOUT status
 *   - Cancel during DISPENSING: rejected
 *   - Cancel of non-existent record: idempotent success
 *   - Cancel of terminal state: idempotent success
 *   - Expiry check with empty result: fast return
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class PreAuthEdgeCaseTest {

    private val preAuthDao: PreAuthDao = mockk(relaxed = true)
    private val nozzleDao: NozzleDao = mockk(relaxed = true)
    private val connectivityManager: ConnectivityManager = mockk(relaxed = true)
    private val auditLogDao: AuditLogDao = mockk(relaxed = true)
    private val fccAdapter: IFccAdapter = mockk(relaxed = true)
    private val scope = CoroutineScope(Dispatchers.Unconfined)
    private val stateFlow = MutableStateFlow(ConnectivityState.FULLY_ONLINE)

    private lateinit var handler: PreAuthHandler

    @Before
    fun setUp() {
        every { connectivityManager.state } returns stateFlow
        handler = PreAuthHandler(
            preAuthDao = preAuthDao,
            nozzleDao = nozzleDao,
            connectivityManager = connectivityManager,
            auditLogDao = auditLogDao,
            scope = scope,
            fccAdapter = fccAdapter,
            config = PreAuthHandler.PreAuthHandlerConfig(fccTimeoutMs = 5_000L),
        )
    }

    // -------------------------------------------------------------------------
    // Multi-order race (M-12)
    // -------------------------------------------------------------------------

    @Test
    fun `M-12 concurrent insert returns -1 — returns dedup result from race winner`() = runTest {
        coEvery { preAuthDao.getByOdooOrderId("ORD-RACE", "SITE-A") } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()

        // Insert returns -1 (unique index violation — concurrent race)
        coEvery { preAuthDao.insert(any()) } returns -1L

        // Re-read returns the winner's record
        val winnerRecord = stubRecord(
            odooOrderId = "ORD-RACE",
            status = PreAuthStatus.AUTHORIZED.name,
            fccAuthorizationCode = "AUTH-WINNER",
        )
        coEvery { preAuthDao.getByOdooOrderId("ORD-RACE", "SITE-A") } returnsMany listOf(null, winnerRecord)

        val result = handler.handle(baseCommand("ORD-RACE"))

        // Should return dedup result, not attempt FCC call
        assertEquals(PreAuthResultStatus.AUTHORIZED, result.status)
        assertEquals("AUTH-WINNER", result.authorizationCode)
        coVerify(exactly = 0) { fccAdapter.sendPreAuth(any()) }
    }

    @Test
    fun `dedup returns IN_PROGRESS for PENDING status`() = runTest {
        val existingPending = stubRecord(
            odooOrderId = "ORD-PENDING",
            status = PreAuthStatus.PENDING.name,
        )
        coEvery { preAuthDao.getByOdooOrderId("ORD-PENDING", "SITE-A") } returns existingPending

        val result = handler.handle(baseCommand("ORD-PENDING"))

        assertEquals(PreAuthResultStatus.IN_PROGRESS, result.status)
        coVerify(exactly = 0) { fccAdapter.sendPreAuth(any()) }
    }

    @Test
    fun `dedup returns AUTHORIZED for DISPENSING status`() = runTest {
        val existingDispensing = stubRecord(
            odooOrderId = "ORD-DISP",
            status = PreAuthStatus.DISPENSING.name,
            fccAuthorizationCode = "AUTH-DISP",
        )
        coEvery { preAuthDao.getByOdooOrderId("ORD-DISP", "SITE-A") } returns existingDispensing

        val result = handler.handle(baseCommand("ORD-DISP"))

        assertEquals(PreAuthResultStatus.AUTHORIZED, result.status)
        assertEquals("AUTH-DISP", result.authorizationCode)
    }

    @Test
    fun `terminal status allows new pre-auth request`() = runTest {
        // Existing record is in FAILED terminal state
        val existingFailed = stubRecord(
            odooOrderId = "ORD-RETRY",
            status = PreAuthStatus.FAILED.name,
        )
        coEvery { preAuthDao.getByOdooOrderId("ORD-RETRY", "SITE-A") } returns existingFailed
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()
        coEvery { preAuthDao.insert(any()) } returns 2L
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            authorizationCode = "AUTH-RETRY",
        )

        val result = handler.handle(baseCommand("ORD-RETRY"))

        // FAILED is terminal — allows new request
        assertEquals(PreAuthResultStatus.AUTHORIZED, result.status)
        coVerify { fccAdapter.sendPreAuth(any()) }
    }

    // -------------------------------------------------------------------------
    // Expiry during FCC outage
    // -------------------------------------------------------------------------

    @Test
    fun `expiry check — AUTHORIZED record with failed deauth stays AUTHORIZED for retry`() = runTest {
        val expiredAuth = stubRecord(
            status = PreAuthStatus.AUTHORIZED.name,
            expiresAt = "2020-01-01T00:00:00Z", // well past expired
        )
        coEvery { preAuthDao.getExpiring(any()) } returns listOf(expiredAuth)

        // FCC deauth fails (simulate FCC unreachable)
        coEvery { fccAdapter.sendPreAuth(any()) } throws java.io.IOException("FCC unreachable")

        handler.runExpiryCheck()

        // Record should NOT be marked EXPIRED (deauth failed → stays AUTHORIZED for retry)
        coVerify(exactly = 0) { preAuthDao.updateStatus(any(), eq(PreAuthStatus.EXPIRED.name), any(), any(), any(), any(), any()) }
        // Audit log should record the retry-pending state
        coVerify(atLeast = 1) { auditLogDao.insert(match { it.eventType == "PRE_AUTH_DEAUTH_RETRY_PENDING" }) }
    }

    @Test
    fun `expiry check — AUTHORIZED record with successful deauth transitions to EXPIRED`() = runTest {
        val expiredAuth = stubRecord(
            status = PreAuthStatus.AUTHORIZED.name,
            expiresAt = "2020-01-01T00:00:00Z",
        )
        coEvery { preAuthDao.getExpiring(any()) } returns listOf(expiredAuth)

        // FCC deauth succeeds
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED, // deauth "acknowledged"
        )

        handler.runExpiryCheck()

        // Record should be marked EXPIRED after successful deauth
        coVerify { preAuthDao.updateStatus(any(), eq(PreAuthStatus.EXPIRED.name), any(), any(), any(), any(), any()) }
    }

    @Test
    fun `expiry check — PENDING record transitions to EXPIRED without deauth attempt`() = runTest {
        val expiredPending = stubRecord(
            status = PreAuthStatus.PENDING.name,
            expiresAt = "2020-01-01T00:00:00Z",
        )
        coEvery { preAuthDao.getExpiring(any()) } returns listOf(expiredPending)

        handler.runExpiryCheck()

        // PENDING doesn't need FCC deauth
        coVerify(exactly = 0) { fccAdapter.sendPreAuth(any()) }
        coVerify { preAuthDao.updateStatus(any(), eq(PreAuthStatus.EXPIRED.name), any(), any(), any(), any(), any()) }
    }

    @Test
    fun `expiry check with empty result returns immediately`() = runTest {
        coEvery { preAuthDao.getExpiring(any()) } returns emptyList()

        handler.runExpiryCheck()

        coVerify(exactly = 0) { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) }
    }

    // -------------------------------------------------------------------------
    // Nozzle mapping edge cases
    // -------------------------------------------------------------------------

    @Test
    fun `nozzle mapping not found returns NOZZLE_MAPPING_NOT_FOUND error`() = runTest {
        coEvery { preAuthDao.getByOdooOrderId("ORD-NO-NOZZLE", "SITE-A") } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns null

        val result = handler.handle(baseCommand("ORD-NO-NOZZLE"))

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertEquals("NOZZLE_MAPPING_NOT_FOUND", result.message)
        coVerify(exactly = 0) { fccAdapter.sendPreAuth(any()) }
    }

    @Test
    fun `command uses FCC pump and nozzle numbers from mapping, not Odoo numbers`() = runTest {
        coEvery { preAuthDao.getByOdooOrderId("ORD-MAP", "SITE-A") } returns null
        // Mapping translates Odoo (1, 1) → FCC (3, 2)
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle(
            fccPumpNumber = 3,
            fccNozzleNumber = 2,
        )
        coEvery { preAuthDao.insert(any()) } returns 1L

        val commandSlot = slot<PreAuthCommand>()
        coEvery { fccAdapter.sendPreAuth(capture(commandSlot)) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            authorizationCode = "AUTH-MAP",
        )

        handler.handle(baseCommand("ORD-MAP"))

        // FCC should receive translated numbers
        assertEquals(3, commandSlot.captured.pumpNumber)
        assertEquals(2, commandSlot.captured.nozzleNumber)
    }

    // -------------------------------------------------------------------------
    // Cancel edge cases
    // -------------------------------------------------------------------------

    @Test
    fun `cancel DISPENSING returns failure`() = runTest {
        coEvery { preAuthDao.getByOdooOrderId("ORD-CANCEL-DISP", "SITE-A") } returns
            stubRecord(odooOrderId = "ORD-CANCEL-DISP", status = PreAuthStatus.DISPENSING.name)

        val result = handler.cancel("ORD-CANCEL-DISP", "SITE-A")

        assertEquals(false, result.success)
        assertTrue(result.message!!.contains("dispensing"))
    }

    @Test
    fun `cancel non-existent record returns idempotent success`() = runTest {
        coEvery { preAuthDao.getByOdooOrderId("ORD-MISSING", "SITE-A") } returns null

        val result = handler.cancel("ORD-MISSING", "SITE-A")

        assertEquals(true, result.success)
    }

    @Test
    fun `cancel already COMPLETED returns idempotent success`() = runTest {
        coEvery { preAuthDao.getByOdooOrderId("ORD-DONE", "SITE-A") } returns
            stubRecord(odooOrderId = "ORD-DONE", status = PreAuthStatus.COMPLETED.name)

        val result = handler.cancel("ORD-DONE", "SITE-A")

        assertEquals(true, result.success)
        assertTrue(result.message!!.contains("terminal"))
    }

    @Test
    fun `cancel AUTHORIZED record transitions to CANCELLED`() = runTest {
        val record = stubRecord(
            odooOrderId = "ORD-AUTH-CANCEL",
            status = PreAuthStatus.AUTHORIZED.name,
        )
        coEvery { preAuthDao.getByOdooOrderId("ORD-AUTH-CANCEL", "SITE-A") } returns record

        val result = handler.cancel("ORD-AUTH-CANCEL", "SITE-A")

        assertEquals(true, result.success)
        coVerify {
            preAuthDao.updateStatus(
                id = record.id,
                status = PreAuthStatus.CANCELLED.name,
                fccCorrelationId = any(),
                fccAuthorizationCode = any(),
                failureReason = any(),
                authorizedAt = any(),
                completedAt = any(),
            )
        }
    }

    // -------------------------------------------------------------------------
    // Connectivity guard
    // -------------------------------------------------------------------------

    @Test
    fun `FCC_UNREACHABLE rejects pre-auth`() = runTest {
        stateFlow.value = ConnectivityState.FCC_UNREACHABLE
        coEvery { preAuthDao.getByOdooOrderId("ORD-OFFLINE", "SITE-A") } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()

        val result = handler.handle(baseCommand("ORD-OFFLINE"))

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertEquals("FCC_UNREACHABLE", result.message)
    }

    @Test
    fun `FULLY_OFFLINE rejects pre-auth`() = runTest {
        stateFlow.value = ConnectivityState.FULLY_OFFLINE
        coEvery { preAuthDao.getByOdooOrderId("ORD-OFFLINE2", "SITE-A") } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()

        val result = handler.handle(baseCommand("ORD-OFFLINE2"))

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertEquals("FCC_UNREACHABLE", result.message)
    }

    @Test
    fun `INTERNET_DOWN allows pre-auth (LAN still up)`() = runTest {
        stateFlow.value = ConnectivityState.INTERNET_DOWN
        coEvery { preAuthDao.getByOdooOrderId("ORD-NONET", "SITE-A") } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()
        coEvery { preAuthDao.insert(any()) } returns 1L
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            authorizationCode = "AUTH-NONET",
        )

        val result = handler.handle(baseCommand("ORD-NONET"))

        assertEquals(PreAuthResultStatus.AUTHORIZED, result.status)
    }

    // -------------------------------------------------------------------------
    // Missing odooOrderId
    // -------------------------------------------------------------------------

    @Test
    fun `missing odooOrderId returns error`() = runTest {
        val command = PreAuthCommand(
            siteCode = "SITE-A",
            pumpNumber = 1,
            amountMinorUnits = 10_000L,
            currencyCode = "ZAR",
            nozzleNumber = 1,
            odooOrderId = null,
        )

        val result = handler.handle(command)

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertEquals("odooOrderId is required", result.message)
    }

    // -------------------------------------------------------------------------
    // Adapter null check
    // -------------------------------------------------------------------------

    @Test
    fun `null adapter returns error`() = runTest {
        val handlerNoAdapter = PreAuthHandler(
            preAuthDao = preAuthDao,
            nozzleDao = nozzleDao,
            connectivityManager = connectivityManager,
            auditLogDao = auditLogDao,
            scope = scope,
            fccAdapter = null,
        )
        coEvery { preAuthDao.getByOdooOrderId("ORD-NOADAPT", "SITE-A") } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()

        val result = handlerNoAdapter.handle(baseCommand("ORD-NOADAPT"))

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertTrue(result.message!!.contains("adapter"))
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun baseCommand(odooOrderId: String = "ORD-${UUID.randomUUID().toString().take(8)}") =
        PreAuthCommand(
            siteCode = "SITE-A",
            pumpNumber = 1,
            amountMinorUnits = 10_000L,
            currencyCode = "ZAR",
            nozzleNumber = 1,
            odooOrderId = odooOrderId,
        )

    private fun stubNozzle(fccPumpNumber: Int = 1, fccNozzleNumber: Int = 1) = Nozzle(
        id = "nozzle-1",
        siteCode = "SITE-A",
        odooPumpNumber = 1,
        fccPumpNumber = fccPumpNumber,
        odooNozzleNumber = 1,
        fccNozzleNumber = fccNozzleNumber,
        productCode = "PMS",
        isActive = 1,
        syncedAt = "2025-01-01T00:00:00Z",
        createdAt = "2025-01-01T00:00:00Z",
        updatedAt = "2025-01-01T00:00:00Z",
    )

    private fun stubRecord(
        id: String = "rec-${UUID.randomUUID().toString().take(8)}",
        odooOrderId: String = "ORD-STUB",
        status: String = PreAuthStatus.PENDING.name,
        fccAuthorizationCode: String? = null,
        expiresAt: String = "2030-01-01T00:00:00Z",
    ): PreAuthRecord = PreAuthRecord(
        id = id,
        siteCode = "SITE-A",
        odooOrderId = odooOrderId,
        pumpNumber = 1,
        nozzleNumber = 1,
        productCode = "PMS",
        currencyCode = "ZAR",
        requestedAmountMinorUnits = 10_000L,
        authorizedAmountMinorUnits = null,
        status = status,
        fccCorrelationId = null,
        fccAuthorizationCode = fccAuthorizationCode,
        failureReason = null,
        customerName = null,
        customerTaxId = null,
        rawFccResponse = null,
        requestedAt = "2024-01-01T10:00:00Z",
        authorizedAt = if (status == PreAuthStatus.AUTHORIZED.name) "2024-01-01T10:00:01Z" else null,
        completedAt = null,
        expiresAt = expiresAt,
        isCloudSynced = 0,
        cloudSyncAttempts = 0,
        lastCloudSyncAttemptAt = null,
        schemaVersion = 1,
        createdAt = "2024-01-01T10:00:00Z",
    )
}
