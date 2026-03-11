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
import com.fccmiddleware.edge.buffer.entity.AuditLog
import com.fccmiddleware.edge.buffer.entity.Nozzle
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import com.fccmiddleware.edge.connectivity.ConnectivityManager
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
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

/**
 * PreAuthHandlerTest — unit tests for EA-2.5 pre-auth handler logic.
 *
 * Validates:
 *   - odooOrderId required
 *   - Local dedup for non-terminal and terminal states
 *   - Nozzle mapping resolution (not found case)
 *   - Connectivity guard (FCC_UNREACHABLE, FULLY_OFFLINE)
 *   - Adapter null check
 *   - Successful FCC call → AUTHORIZED result + DB update
 *   - Failed FCC call (DECLINED, TIMEOUT) → FAILED DB update
 *   - Cancel: DISPENSING rejection, PENDING/AUTHORIZED → CANCELLED, terminal idempotency
 *   - Expiry check: no-op on empty, transitions expired records to EXPIRED
 */
@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class PreAuthHandlerTest {

    private lateinit var preAuthDao: PreAuthDao
    private lateinit var nozzleDao: NozzleDao
    private lateinit var connectivityManager: ConnectivityManager
    private lateinit var auditLogDao: AuditLogDao
    private lateinit var fccAdapter: IFccAdapter
    private val connectivityState = MutableStateFlow(ConnectivityState.FULLY_ONLINE)
    private val handlerScope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)

    // Fast timeout so tests don't wait 30 s
    private val testConfig = PreAuthHandler.PreAuthHandlerConfig(
        fccTimeoutMs = 5_000L,
        defaultPreAuthTtlSeconds = 300L,
    )

    @Before
    fun setUp() {
        preAuthDao = mockk()
        nozzleDao = mockk()
        connectivityManager = mockk()
        auditLogDao = mockk()
        fccAdapter = mockk()

        every { connectivityManager.state } returns connectivityState
        coEvery { auditLogDao.insert(any()) } returns 1L
    }

    // -------------------------------------------------------------------------
    // handle — odooOrderId required
    // -------------------------------------------------------------------------

    @Test
    fun `handle returns ERROR when odooOrderId is null`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val command = baseCommand().copy(odooOrderId = null)

        val result = handler.handle(command)

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertNotNull(result.message)
    }

    // -------------------------------------------------------------------------
    // handle — dedup (non-terminal existing record)
    // -------------------------------------------------------------------------

    @Test
    fun `handle returns AUTHORIZED result for existing AUTHORIZED record (dedup)`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val existing = stubRecord(status = PreAuthStatus.AUTHORIZED.name, authCode = "AUTH-001")
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns existing

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.AUTHORIZED, result.status)
        assertEquals("AUTH-001", result.authorizationCode)
    }

    @Test
    fun `handle returns AUTHORIZED result for existing DISPENSING record (dedup)`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val existing = stubRecord(status = PreAuthStatus.DISPENSING.name, authCode = "AUTH-002")
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns existing

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.AUTHORIZED, result.status)
        assertEquals("AUTH-002", result.authorizationCode)
    }

    @Test
    fun `handle returns ERROR result for existing PENDING record (dedup in-progress)`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val existing = stubRecord(status = PreAuthStatus.PENDING.name, authCode = null)
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns existing

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertNotNull(result.message)
        // Must NOT call nozzle lookup or FCC for a dedup hit
        coVerify(exactly = 0) { nozzleDao.resolveForPreAuth(any(), any(), any()) }
        coVerify(exactly = 0) { fccAdapter.sendPreAuth(any()) }
    }

    // -------------------------------------------------------------------------
    // handle — dedup allows new request for terminal states
    // -------------------------------------------------------------------------

    @Test
    fun `handle proceeds to FCC call when existing record is FAILED (terminal)`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns stubRecord(status = PreAuthStatus.FAILED.name)
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()
        coEvery { preAuthDao.insert(any()) } returns 1L
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            authorizationCode = "NEW-AUTH",
            expiresAtUtc = "2030-01-01T00:00:00Z",
        )

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.AUTHORIZED, result.status)
        coVerify(exactly = 1) { fccAdapter.sendPreAuth(any()) }
    }

    @Test
    fun `handle proceeds to FCC call when no existing record`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()
        coEvery { preAuthDao.insert(any()) } returns 1L
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            authorizationCode = "AUTH-NEW",
        )

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.AUTHORIZED, result.status)
    }

    // -------------------------------------------------------------------------
    // handle — nozzle resolution
    // -------------------------------------------------------------------------

    @Test
    fun `handle returns NOZZLE_MAPPING_NOT_FOUND when nozzle not found`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns null

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertEquals("NOZZLE_MAPPING_NOT_FOUND", result.message)
        coVerify(exactly = 0) { fccAdapter.sendPreAuth(any()) }
    }

    // -------------------------------------------------------------------------
    // handle — connectivity guard
    // -------------------------------------------------------------------------

    @Test
    fun `handle returns FCC_UNREACHABLE when connectivity is FCC_UNREACHABLE`() = runTest {
        connectivityState.value = ConnectivityState.FCC_UNREACHABLE
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertEquals("FCC_UNREACHABLE", result.message)
        coVerify(exactly = 0) { fccAdapter.sendPreAuth(any()) }
    }

    @Test
    fun `handle returns FCC_UNREACHABLE when connectivity is FULLY_OFFLINE`() = runTest {
        connectivityState.value = ConnectivityState.FULLY_OFFLINE
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertEquals("FCC_UNREACHABLE", result.message)
    }

    @Test
    fun `handle proceeds when connectivity is INTERNET_DOWN (LAN still up)`() = runTest {
        connectivityState.value = ConnectivityState.INTERNET_DOWN
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()
        coEvery { preAuthDao.insert(any()) } returns 1L
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            authorizationCode = "AUTH-LAN",
        )

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.AUTHORIZED, result.status)
    }

    // -------------------------------------------------------------------------
    // handle — adapter null check
    // -------------------------------------------------------------------------

    @Test
    fun `handle returns error when fccAdapter is null`() = runTest {
        val handler = buildHandler(adapter = null)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()

        val result = handler.handle(baseCommand())

        assertEquals(PreAuthResultStatus.ERROR, result.status)
        assertNotNull(result.message)
    }

    // -------------------------------------------------------------------------
    // handle — FCC call + DB update
    // -------------------------------------------------------------------------

    @Test
    fun `handle calls FCC with FCC pump+nozzle numbers (not Odoo numbers)`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val nozzle = stubNozzle(fccPumpNumber = 5, fccNozzleNumber = 2)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns nozzle
        coEvery { preAuthDao.insert(any()) } returns 1L
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit

        val commandSlot = slot<PreAuthCommand>()
        coEvery { fccAdapter.sendPreAuth(capture(commandSlot)) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            authorizationCode = "AUTH-X",
        )

        handler.handle(baseCommand())

        assertEquals(5, commandSlot.captured.pumpNumber)
        assertEquals(2, commandSlot.captured.nozzleNumber)
    }

    @Test
    fun `handle updates DB to AUTHORIZED on FCC AUTHORIZED response`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()
        coEvery { preAuthDao.insert(any()) } returns 1L
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            authorizationCode = "AUTH-123",
            expiresAtUtc = "2030-01-01T00:05:00Z",
        )

        val statusSlot = slot<String>()
        val authCodeSlot = slot<String>()
        coEvery {
            preAuthDao.updateStatus(any(), capture(statusSlot), any(), capture(authCodeSlot), any(), any(), any())
        } returns Unit

        handler.handle(baseCommand())

        assertEquals(PreAuthStatus.AUTHORIZED.name, statusSlot.captured)
        assertEquals("AUTH-123", authCodeSlot.captured)
    }

    @Test
    fun `handle updates DB to FAILED on FCC DECLINED response`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()
        coEvery { preAuthDao.insert(any()) } returns 1L
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.DECLINED,
            message = "Pump offline",
        )

        val statusSlot = slot<String>()
        coEvery {
            preAuthDao.updateStatus(any(), capture(statusSlot), any(), any(), any(), any(), any())
        } returns Unit

        handler.handle(baseCommand())

        assertEquals(PreAuthStatus.FAILED.name, statusSlot.captured)
    }

    @Test
    fun `handle updates DB to FAILED on FCC TIMEOUT`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()
        coEvery { preAuthDao.insert(any()) } returns 1L
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.TIMEOUT,
            message = "FCC timeout",
        )

        val statusSlot = slot<String>()
        coEvery {
            preAuthDao.updateStatus(any(), capture(statusSlot), any(), any(), any(), any(), any())
        } returns Unit

        handler.handle(baseCommand())

        assertEquals(PreAuthStatus.FAILED.name, statusSlot.captured)
    }

    @Test
    fun `handle writes audit log entry asynchronously`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId(any(), any()) } returns null
        coEvery { nozzleDao.resolveForPreAuth("SITE-A", 1, 1) } returns stubNozzle()
        coEvery { preAuthDao.insert(any()) } returns 1L
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(
            status = PreAuthResultStatus.AUTHORIZED,
            authorizationCode = "AUTH-LOG",
        )

        handler.handle(baseCommand())

        val logSlot = slot<AuditLog>()
        coVerify(atLeast = 1) { auditLogDao.insert(capture(logSlot)) }
        assertEquals("PRE_AUTH_HANDLED", logSlot.captured.eventType)
    }

    // -------------------------------------------------------------------------
    // cancel
    // -------------------------------------------------------------------------

    @Test
    fun `cancel returns success when no record found (idempotent)`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns null

        val result = handler.cancel("order-1", "SITE-A")

        assertTrue(result.success)
    }

    @Test
    fun `cancel returns failure for DISPENSING record`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns stubRecord(status = PreAuthStatus.DISPENSING.name)

        val result = handler.cancel("order-1", "SITE-A")

        assertFalse(result.success)
        assertNotNull(result.message)
        coVerify(exactly = 0) { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) }
    }

    @Test
    fun `cancel transitions PENDING record to CANCELLED`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns stubRecord(status = PreAuthStatus.PENDING.name)

        val statusSlot = slot<String>()
        coEvery {
            preAuthDao.updateStatus(any(), capture(statusSlot), any(), any(), any(), any(), any())
        } returns Unit

        val result = handler.cancel("order-1", "SITE-A")

        assertTrue(result.success)
        assertEquals(PreAuthStatus.CANCELLED.name, statusSlot.captured)
    }

    @Test
    fun `cancel transitions AUTHORIZED record to CANCELLED`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns stubRecord(
            status = PreAuthStatus.AUTHORIZED.name,
            authCode = "AUTH-TO-CANCEL",
        )

        val statusSlot = slot<String>()
        coEvery {
            preAuthDao.updateStatus(any(), capture(statusSlot), any(), any(), any(), any(), any())
        } returns Unit

        val result = handler.cancel("order-1", "SITE-A")

        assertTrue(result.success)
        assertEquals(PreAuthStatus.CANCELLED.name, statusSlot.captured)
    }

    @Test
    fun `cancel returns success for already terminal COMPLETED record (idempotent)`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns stubRecord(status = PreAuthStatus.COMPLETED.name)

        val result = handler.cancel("order-1", "SITE-A")

        assertTrue(result.success)
        coVerify(exactly = 0) { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) }
    }

    @Test
    fun `cancel writes audit log for PENDING to CANCELLED transition`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getByOdooOrderId("order-1", "SITE-A") } returns stubRecord(status = PreAuthStatus.PENDING.name)
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit

        handler.cancel("order-1", "SITE-A")

        val logSlot = slot<AuditLog>()
        coVerify(atLeast = 1) { auditLogDao.insert(capture(logSlot)) }
        assertEquals("PRE_AUTH_CANCELLED", logSlot.captured.eventType)
    }

    // -------------------------------------------------------------------------
    // runExpiryCheck
    // -------------------------------------------------------------------------

    @Test
    fun `runExpiryCheck does nothing when no expired records`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        coEvery { preAuthDao.getExpiring(any()) } returns emptyList()

        handler.runExpiryCheck()

        coVerify(exactly = 0) { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) }
        coVerify(exactly = 0) { auditLogDao.insert(any()) }
    }

    @Test
    fun `runExpiryCheck transitions expired PENDING records to EXPIRED`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val expiredRecord = stubRecord(status = PreAuthStatus.PENDING.name)
        coEvery { preAuthDao.getExpiring(any()) } returns listOf(expiredRecord)

        val statusSlot = slot<String>()
        coEvery {
            preAuthDao.updateStatus(any(), capture(statusSlot), any(), any(), any(), any(), any())
        } returns Unit

        handler.runExpiryCheck()

        assertEquals(PreAuthStatus.EXPIRED.name, statusSlot.captured)
    }

    @Test
    fun `runExpiryCheck transitions expired AUTHORIZED records to EXPIRED`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val expiredRecord = stubRecord(status = PreAuthStatus.AUTHORIZED.name, authCode = "OLD-AUTH")
        coEvery { preAuthDao.getExpiring(any()) } returns listOf(expiredRecord)
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(status = PreAuthResultStatus.DECLINED)

        val statusSlot = slot<String>()
        coEvery {
            preAuthDao.updateStatus(any(), capture(statusSlot), any(), any(), any(), any(), any())
        } returns Unit

        handler.runExpiryCheck()

        assertEquals(PreAuthStatus.EXPIRED.name, statusSlot.captured)
    }

    @Test
    fun `runExpiryCheck attempts FCC deauth for AUTHORIZED expired records when adapter available`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val expiredRecord = stubRecord(status = PreAuthStatus.AUTHORIZED.name)
        coEvery { preAuthDao.getExpiring(any()) } returns listOf(expiredRecord)
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit
        coEvery { fccAdapter.sendPreAuth(any()) } returns PreAuthResult(status = PreAuthResultStatus.DECLINED)

        handler.runExpiryCheck()

        coVerify(exactly = 1) { fccAdapter.sendPreAuth(any()) }
    }

    @Test
    fun `runExpiryCheck does NOT call FCC for PENDING expired records`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val expiredRecord = stubRecord(status = PreAuthStatus.PENDING.name)
        coEvery { preAuthDao.getExpiring(any()) } returns listOf(expiredRecord)
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit

        handler.runExpiryCheck()

        coVerify(exactly = 0) { fccAdapter.sendPreAuth(any()) }
    }

    @Test
    fun `runExpiryCheck writes audit log for each expired record`() = runTest {
        val handler = buildHandler(adapter = null)
        val record1 = stubRecord(id = "id-1", status = PreAuthStatus.PENDING.name)
        val record2 = stubRecord(id = "id-2", status = PreAuthStatus.PENDING.name)
        coEvery { preAuthDao.getExpiring(any()) } returns listOf(record1, record2)
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit

        handler.runExpiryCheck()

        coVerify(exactly = 2) { auditLogDao.insert(any()) }
    }

    @Test
    fun `runExpiryCheck continues processing remaining records if FCC deauth throws`() = runTest {
        val handler = buildHandler(adapter = fccAdapter)
        val record1 = stubRecord(id = "id-1", status = PreAuthStatus.AUTHORIZED.name)
        val record2 = stubRecord(id = "id-2", status = PreAuthStatus.AUTHORIZED.name)
        coEvery { preAuthDao.getExpiring(any()) } returns listOf(record1, record2)
        coEvery { preAuthDao.updateStatus(any(), any(), any(), any(), any(), any(), any()) } returns Unit
        coEvery { fccAdapter.sendPreAuth(any()) } throws RuntimeException("FCC connection error")

        handler.runExpiryCheck()

        // Both records must still be transitioned to EXPIRED despite FCC throwing
        coVerify(exactly = 2) {
            preAuthDao.updateStatus(any(), PreAuthStatus.EXPIRED.name, any(), any(), any(), any(), any())
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun buildHandler(adapter: IFccAdapter?) = PreAuthHandler(
        preAuthDao = preAuthDao,
        nozzleDao = nozzleDao,
        connectivityManager = connectivityManager,
        auditLogDao = auditLogDao,
        scope = handlerScope,
        fccAdapter = adapter,
        config = testConfig,
    )

    private fun baseCommand() = PreAuthCommand(
        siteCode = "SITE-A",
        pumpNumber = 1,
        nozzleNumber = 1,
        amountMinorUnits = 50_00L, // 50.00 ZMW in minor units
        currencyCode = "ZMW",
        odooOrderId = "order-1",
        customerTaxId = null,
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
        id: String = "rec-1",
        status: String = PreAuthStatus.PENDING.name,
        authCode: String? = null,
    ) = PreAuthRecord(
        id = id,
        siteCode = "SITE-A",
        odooOrderId = "order-1",
        pumpNumber = 1,
        nozzleNumber = 1,
        productCode = "PMS",
        currencyCode = "ZMW",
        requestedAmountMinorUnits = 50_00L,
        authorizedAmountMinorUnits = null,
        status = status,
        fccCorrelationId = null,
        fccAuthorizationCode = authCode,
        failureReason = null,
        customerName = null,
        customerTaxId = null,
        rawFccResponse = null,
        requestedAt = "2025-01-01T08:00:00Z",
        authorizedAt = null,
        completedAt = null,
        expiresAt = "2025-01-01T08:05:00Z",
        isCloudSynced = 0,
        cloudSyncAttempts = 0,
        lastCloudSyncAttemptAt = null,
        schemaVersion = 1,
        createdAt = "2025-01-01T08:00:00Z",
    )
}
