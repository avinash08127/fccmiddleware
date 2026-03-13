package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.adapter.common.PreAuthStatus
import com.fccmiddleware.edge.buffer.dao.PreAuthDao
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import com.fccmiddleware.edge.security.KeystoreBackedStringCipher
import com.fccmiddleware.edge.security.KeystoreManager
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
 * PreAuthCloudForwardWorkerTest — unit tests for EA-3.6 Pre-Auth Cloud Forwarding.
 *
 * Validates:
 *   - No-ops when any required dependency is null
 *   - No-ops when device is decommissioned
 *   - No-ops when backoff is active
 *   - No-ops when no unsynced records
 *   - No-ops when access token is unavailable
 *   - Success (201) → markCloudSynced(), failure count reset
 *   - Conflict (409 INVALID_TRANSITION) → markCloudSynced() with warning
 *   - Conflict (409 RACE_CONDITION) → recordCloudSyncFailure(), backoff applied
 *   - 401 → token refresh → retry succeeds → markCloudSynced()
 *   - 401 → token refresh fails → recordCloudSyncFailure(), backoff applied
 *   - 403 DEVICE_DECOMMISSIONED → markDecommissioned(), stops processing
 *   - 403 non-decommission → recordCloudSyncFailure(), backoff applied
 *   - Transport error → recordCloudSyncFailure(), backoff, stops remaining batch
 *   - Multiple records: stops on first transport failure
 *   - Multiple records: all forwarded on success
 *   - Backoff calculation: exponential with cap
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class PreAuthCloudForwardWorkerTest {

    private val preAuthDao: PreAuthDao = mockk(relaxed = true)
    private val cloudApiClient: CloudApiClient = mockk()
    private val tokenProvider: DeviceTokenProvider = mockk()
    private val keystoreManager: KeystoreManager = mockk()

    private val config = PreAuthCloudForwardWorkerConfig(
        batchSize = 10,
        baseBackoffMs = 1_000L,
        maxBackoffMs = 60_000L,
    )

    private lateinit var worker: PreAuthCloudForwardWorker

    @Before
    fun setUp() {
        worker = PreAuthCloudForwardWorker(
            preAuthDao = preAuthDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            keystoreManager = keystoreManager,
            config = config,
        )
        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.markDecommissioned() } just Runs
        every { tokenProvider.getAccessToken() } returns "valid-jwt-token"
        every { keystoreManager.storeSecret(any(), any()) } answers {
            secondArg<String>().toByteArray(Charsets.UTF_8)
        }
        every { keystoreManager.retrieveSecret(any(), any()) } answers {
            String(secondArg<ByteArray>(), Charsets.UTF_8)
        }
    }

    // -------------------------------------------------------------------------
    // No-op guards
    // -------------------------------------------------------------------------

    @Test
    fun `returns early when preAuthDao is null`() = runTest {
        val w = PreAuthCloudForwardWorker(
            preAuthDao = null,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )
        w.forwardUnsyncedPreAuths()
        coVerify(exactly = 0) { cloudApiClient.forwardPreAuth(any(), any()) }
    }

    @Test
    fun `returns early when cloudApiClient is null`() = runTest {
        val w = PreAuthCloudForwardWorker(
            preAuthDao = preAuthDao,
            cloudApiClient = null,
            tokenProvider = tokenProvider,
        )
        w.forwardUnsyncedPreAuths()
        coVerify(exactly = 0) { preAuthDao.getUnsynced(any()) }
    }

    @Test
    fun `returns early when tokenProvider is null`() = runTest {
        val w = PreAuthCloudForwardWorker(
            preAuthDao = preAuthDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = null,
        )
        w.forwardUnsyncedPreAuths()
        coVerify(exactly = 0) { preAuthDao.getUnsynced(any()) }
    }

    @Test
    fun `returns early when device is decommissioned`() = runTest {
        every { tokenProvider.isDecommissioned() } returns true
        worker.forwardUnsyncedPreAuths()
        coVerify(exactly = 0) { preAuthDao.getUnsynced(any()) }
    }

    @Test
    fun `returns early when backoff is active`() = runTest {
        worker.circuitBreaker.nextRetryAt = Instant.now().plusSeconds(60)
        worker.forwardUnsyncedPreAuths()
        coVerify(exactly = 0) { preAuthDao.getUnsynced(any()) }
    }

    @Test
    fun `returns early when no unsynced records`() = runTest {
        coEvery { preAuthDao.getUnsynced(any()) } returns emptyList()
        worker.forwardUnsyncedPreAuths()
        coVerify(exactly = 0) { cloudApiClient.forwardPreAuth(any(), any()) }
    }

    @Test
    fun `empty batch resets backoff so new records forward immediately (H-02)`() = runTest {
        // Seed prior failure state
        worker.circuitBreaker.consecutiveFailureCount = 3
        worker.circuitBreaker.nextRetryAt = Instant.EPOCH // expired so we pass the guard

        coEvery { preAuthDao.getUnsynced(any()) } returns emptyList()
        worker.forwardUnsyncedPreAuths()

        assertEquals("Failure count should be reset", 0, worker.consecutiveFailureCount)
        assertEquals("nextRetryAt should be reset", Instant.EPOCH, worker.nextRetryAt)
    }

    @Test
    fun `returns early when access token is null`() = runTest {
        every { tokenProvider.getAccessToken() } returns null
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(makePreAuth())
        worker.forwardUnsyncedPreAuths()
        coVerify(exactly = 0) { cloudApiClient.forwardPreAuth(any(), any()) }
    }

    // -------------------------------------------------------------------------
    // Successful forwarding
    // -------------------------------------------------------------------------

    @Test
    fun `success marks record as cloud synced and resets failure count`() = runTest {
        val record = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.Success(makeForwardResponse(record))

        worker.forwardUnsyncedPreAuths()

        coVerify { preAuthDao.markCloudSynced(record.id, any()) }
        assertEquals(0, worker.consecutiveFailureCount)
        assertEquals(Instant.EPOCH, worker.nextRetryAt)
    }

    @Test
    fun `conflict invalid transition 409 marks record as cloud synced`() = runTest {
        val record = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.Conflict("CONFLICT.INVALID_TRANSITION", "Already in terminal state")

        worker.forwardUnsyncedPreAuths()

        coVerify { preAuthDao.markCloudSynced(record.id, any()) }
        assertEquals(0, worker.consecutiveFailureCount)
    }

    @Test
    fun `conflict race condition 409 records failure and leaves record unsynced`() = runTest {
        val record = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.Conflict("CONFLICT.RACE_CONDITION", "Concurrent insert")

        worker.forwardUnsyncedPreAuths()

        coVerify(exactly = 0) { preAuthDao.markCloudSynced(any(), any()) }
        coVerify { preAuthDao.recordCloudSyncFailure(record.id, any()) }
        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.EPOCH))
    }

    @Test
    fun `multiple records all forwarded on success`() = runTest {
        val r1 = makePreAuth()
        val r2 = makePreAuth()
        val r3 = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(r1, r2, r3)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.Success(makeForwardResponse(r1))

        worker.forwardUnsyncedPreAuths()

        coVerify(exactly = 3) { cloudApiClient.forwardPreAuth(any(), any()) }
        coVerify { preAuthDao.markCloudSynced(r1.id, any()) }
        coVerify { preAuthDao.markCloudSynced(r2.id, any()) }
        coVerify { preAuthDao.markCloudSynced(r3.id, any()) }
    }

    @Test
    fun `forward request maps PreAuthRecord fields correctly`() = runTest {
        val record = makePreAuth(
            siteCode = "SITE-42",
            odooOrderId = "ORD-999",
            pumpNumber = 3,
            nozzleNumber = 2,
            productCode = "AGO",
            requestedAmount = 50_000L,
            unitPrice = 8_150L,
            currencyCode = "TZS",
            status = "AUTHORIZED",
            fccCorrelationId = "FCC-CORR-123",
            fccAuthorizationCode = "AUTH-456",
            customerName = "Test Customer",
            customerTaxId = "TIN-123456789",
        )
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)

        val requestSlot = slot<PreAuthForwardRequest>()
        coEvery { cloudApiClient.forwardPreAuth(capture(requestSlot), any()) } returns
            CloudPreAuthForwardResult.Success(makeForwardResponse(record))

        worker.forwardUnsyncedPreAuths()

        val req = requestSlot.captured
        assertEquals("SITE-42", req.siteCode)
        assertEquals("ORD-999", req.odooOrderId)
        assertEquals(3, req.pumpNumber)
        assertEquals(2, req.nozzleNumber)
        assertEquals("AGO", req.productCode)
        assertEquals(50_000L, req.requestedAmount)
        assertEquals(8_150L, req.unitPrice)
        assertEquals("TZS", req.currency)
        assertEquals("AUTHORIZED", req.status)
        assertEquals("FCC-CORR-123", req.fccCorrelationId)
        assertEquals("AUTH-456", req.fccAuthorizationCode)
        assertEquals("Test Customer", req.customerName)
        assertEquals("TIN-123456789", req.customerTaxId)
        coVerify {
            preAuthDao.updateCustomerTaxId(
                record.id,
                match { it?.startsWith(KeystoreBackedStringCipher.ENCRYPTED_PREFIX_V1) == true },
            )
        }
    }

    @Test
    fun `encrypted customerTaxId is decrypted before cloud forward`() = runTest {
        val cipher = KeystoreBackedStringCipher(keystoreManager, KeystoreManager.ALIAS_PREAUTH_PII)
        val record = makePreAuth(customerTaxId = cipher.encryptForStorage("TIN-ENCRYPTED-123"))
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)

        val requestSlot = slot<PreAuthForwardRequest>()
        coEvery { cloudApiClient.forwardPreAuth(capture(requestSlot), any()) } returns
            CloudPreAuthForwardResult.Success(makeForwardResponse(record))

        worker.forwardUnsyncedPreAuths()

        assertEquals("TIN-ENCRYPTED-123", requestSlot.captured.customerTaxId)
        coVerify(exactly = 0) { preAuthDao.updateCustomerTaxId(record.id, any()) }
    }

    // -------------------------------------------------------------------------
    // 401 — token refresh
    // -------------------------------------------------------------------------

    @Test
    fun `401 triggers token refresh and retry, marks synced on success`() = runTest {
        val record = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)
        coEvery { tokenProvider.refreshAccessToken() } returns true
        every { tokenProvider.getAccessToken() } returnsMany listOf("old-token", "new-token")

        coEvery { cloudApiClient.forwardPreAuth(any(), "old-token") } returns
            CloudPreAuthForwardResult.Unauthorized
        coEvery { cloudApiClient.forwardPreAuth(any(), "new-token") } returns
            CloudPreAuthForwardResult.Success(makeForwardResponse(record))

        worker.forwardUnsyncedPreAuths()

        coVerify { tokenProvider.refreshAccessToken() }
        coVerify { preAuthDao.markCloudSynced(record.id, any()) }
        assertEquals(0, worker.consecutiveFailureCount)
    }

    @Test
    fun `401 records failure when token refresh fails`() = runTest {
        val record = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.Unauthorized
        coEvery { tokenProvider.refreshAccessToken() } returns false

        worker.forwardUnsyncedPreAuths()

        coVerify { preAuthDao.recordCloudSyncFailure(record.id, any()) }
        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.EPOCH))
    }

    // -------------------------------------------------------------------------
    // 403 — decommission and other forbidden
    // -------------------------------------------------------------------------

    @Test
    fun `403 DEVICE_DECOMMISSIONED calls markDecommissioned and stops`() = runTest {
        val record = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.Forbidden("DEVICE_DECOMMISSIONED")

        worker.forwardUnsyncedPreAuths()

        coVerify { tokenProvider.markDecommissioned() }
        coVerify(exactly = 0) { preAuthDao.markCloudSynced(any(), any()) }
        assertEquals(0, worker.consecutiveFailureCount)
    }

    @Test
    fun `403 non-decommission records failure with backoff`() = runTest {
        val record = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.Forbidden("SITE_MISMATCH")

        worker.forwardUnsyncedPreAuths()

        coVerify { preAuthDao.recordCloudSyncFailure(record.id, any()) }
        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.EPOCH))
    }

    // -------------------------------------------------------------------------
    // Transport errors and backoff
    // -------------------------------------------------------------------------

    @Test
    fun `transport error records failure and applies backoff`() = runTest {
        val record = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.TransportError("Connection refused")

        worker.forwardUnsyncedPreAuths()

        coVerify { preAuthDao.recordCloudSyncFailure(record.id, any()) }
        assertEquals(1, worker.consecutiveFailureCount)
        assertTrue(worker.nextRetryAt.isAfter(Instant.EPOCH))
    }

    @Test
    fun `transport error stops processing remaining batch`() = runTest {
        val r1 = makePreAuth()
        val r2 = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(r1, r2)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.TransportError("Timeout")

        worker.forwardUnsyncedPreAuths()

        // Only one record attempted — worker stops on first failure
        coVerify(exactly = 1) { cloudApiClient.forwardPreAuth(any(), any()) }
        coVerify { preAuthDao.recordCloudSyncFailure(r1.id, any()) }
        coVerify(exactly = 0) { preAuthDao.recordCloudSyncFailure(r2.id, any()) }
    }

    @Test
    fun `legacy record without unit price is not forwarded with fabricated placeholder`() = runTest {
        val record = makePreAuth(unitPrice = null)
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)

        worker.forwardUnsyncedPreAuths()

        coVerify(exactly = 0) { cloudApiClient.forwardPreAuth(any(), any()) }
        coVerify { preAuthDao.markCloudSynced(record.id, any()) }
    }

    @Test
    fun `successful forward after failures resets backoff`() = runTest {
        worker.circuitBreaker.consecutiveFailureCount = 3
        worker.circuitBreaker.nextRetryAt = Instant.EPOCH

        val record = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(record)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.Success(makeForwardResponse(record))

        worker.forwardUnsyncedPreAuths()

        assertEquals(0, worker.consecutiveFailureCount)
        assertEquals(Instant.EPOCH, worker.nextRetryAt)
    }

    // -------------------------------------------------------------------------
    // PA-P04: Bounded concurrency
    // -------------------------------------------------------------------------

    @Test
    fun `transport error stops remaining records via shouldStop flag (PA-P04)`() = runTest {
        // With concurrency=1 the stop-on-failure is strictly serial (easiest to assert)
        val serialWorker = PreAuthCloudForwardWorker(
            preAuthDao = preAuthDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
            keystoreManager = keystoreManager,
            config = PreAuthCloudForwardWorkerConfig(batchSize = 10, maxConcurrency = 1),
        )
        val r1 = makePreAuth()
        val r2 = makePreAuth()
        val r3 = makePreAuth()
        coEvery { preAuthDao.getUnsynced(any()) } returns listOf(r1, r2, r3)
        coEvery { cloudApiClient.forwardPreAuth(any(), any()) } returns
            CloudPreAuthForwardResult.TransportError("timeout")

        serialWorker.forwardUnsyncedPreAuths()

        // Only r1 should be attempted; r2 and r3 see shouldStop=true and exit early
        coVerify(exactly = 1) { cloudApiClient.forwardPreAuth(any(), any()) }
        coVerify { preAuthDao.recordCloudSyncFailure(r1.id, any()) }
        coVerify(exactly = 0) { preAuthDao.recordCloudSyncFailure(r2.id, any()) }
        coVerify(exactly = 0) { preAuthDao.recordCloudSyncFailure(r3.id, any()) }
    }

    // -------------------------------------------------------------------------
    // Backoff calculation
    // -------------------------------------------------------------------------

    @Test
    fun `circuit breaker backoff doubles with each failure up to max`() = runTest {
        val cb = worker.circuitBreaker
        cb.resetOnConnectivityRecovery()
        assertEquals(1_000L, cb.recordFailure())
        assertEquals(2_000L, cb.recordFailure())
        assertEquals(4_000L, cb.recordFailure())
        assertEquals(8_000L, cb.recordFailure())
    }

    @Test
    fun `circuit breaker backoff caps at maxBackoffMs`() = runTest {
        val cb = worker.circuitBreaker
        cb.resetOnConnectivityRecovery()
        repeat(6) { cb.recordFailure() }
        assertEquals(60_000L, cb.recordFailure()) // failure 7
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun makePreAuth(
        id: String = UUID.randomUUID().toString(),
        siteCode: String = "SITE-001",
        odooOrderId: String = "ORD-${UUID.randomUUID().toString().take(8)}",
        pumpNumber: Int = 1,
        nozzleNumber: Int = 1,
        productCode: String = "PMS",
        requestedAmount: Long = 10_000L,
        unitPrice: Long? = 1_500L,
        currencyCode: String = "NGN",
        status: String = "AUTHORIZED",
        fccCorrelationId: String? = null,
        fccAuthorizationCode: String? = null,
        customerName: String? = null,
        customerTaxId: String? = null,
    ): PreAuthRecord = PreAuthRecord(
        id = id,
        siteCode = siteCode,
        odooOrderId = odooOrderId,
        pumpNumber = pumpNumber,
        nozzleNumber = nozzleNumber,
        productCode = productCode,
        currencyCode = currencyCode,
        requestedAmountMinorUnits = requestedAmount,
        unitPrice = unitPrice,
        authorizedAmountMinorUnits = null,
        status = PreAuthStatus.valueOf(status),
        fccCorrelationId = fccCorrelationId,
        fccAuthorizationCode = fccAuthorizationCode,
        failureReason = null,
        customerName = customerName,
        customerTaxId = customerTaxId,
        rawFccResponse = null,
        requestedAt = "2024-01-01T10:00:00Z",
        authorizedAt = "2024-01-01T10:00:01Z",
        completedAt = null,
        expiresAt = "2024-01-01T10:05:00Z",
        isCloudSynced = 0,
        cloudSyncAttempts = 0,
        lastCloudSyncAttemptAt = null,
        schemaVersion = 1,
        createdAt = "2024-01-01T10:00:00Z",
    )

    private fun makeForwardResponse(record: PreAuthRecord): PreAuthForwardResponse =
        PreAuthForwardResponse(
            id = UUID.randomUUID().toString(),
            status = record.status.name,
            siteCode = record.siteCode,
            odooOrderId = record.odooOrderId,
        )
}
