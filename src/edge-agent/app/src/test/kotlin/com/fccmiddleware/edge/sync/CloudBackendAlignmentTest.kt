package com.fccmiddleware.edge.sync

import com.fccmiddleware.edge.buffer.TransactionBufferManager
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.config.ConfigApplyResult
import com.fccmiddleware.edge.config.ConfigManager
import com.fccmiddleware.edge.config.EdgeAgentConfigDto
import io.mockk.Runs
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.just
import io.mockk.mockk
import io.mockk.slot
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant
import java.util.UUID

/**
 * CloudBackendAlignmentTest — validates cloud schema compatibility:
 *   - Schema format validation: upload request matches cloud contract
 *   - Dedup handshake: DUPLICATE outcome correctly handled
 *   - Config version compatibility: version gating, ETag negotiation
 *   - Unknown fields in cloud responses: ignored by lenient parser
 *   - Missing optional fields: parsed correctly
 *   - CloudUploadWorker request construction: legalEntityId, isDuplicate, ordering
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class CloudBackendAlignmentTest {

    private val json = Json {
        ignoreUnknownKeys = true
        isLenient = true
    }

    // -------------------------------------------------------------------------
    // Upload request schema compliance
    // -------------------------------------------------------------------------

    @Test
    fun `upload request transaction DTO includes all required schema fields`() = runTest {
        val bufferManager: TransactionBufferManager = mockk(relaxed = true)
        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val cloudApiClient: CloudApiClient = mockk()
        val tokenProvider: DeviceTokenProvider = mockk()

        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns "token"
        every { tokenProvider.getLegalEntityId() } returns "lei-test-123"
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit

        val worker = CloudUploadWorker(
            bufferManager = bufferManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )

        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)

        val requestSlot = slot<CloudUploadRequest>()
        coEvery { cloudApiClient.uploadBatch(capture(requestSlot), any()) } returns CloudUploadResult.Success(
            CloudUploadResponse(
                results = listOf(
                    CloudUploadRecordResult(
                        fccTransactionId = tx.fccTransactionId,
                        siteCode = tx.siteCode,
                        outcome = "ACCEPTED",
                        id = UUID.randomUUID().toString(),
                    ),
                ),
                acceptedCount = 1,
                duplicateCount = 0,
                rejectedCount = 0,
            ),
        )

        worker.uploadPendingBatch()

        val dto = requestSlot.captured.transactions.first()
        assertEquals(tx.id, dto.id)
        assertEquals(tx.fccTransactionId, dto.fccTransactionId)
        assertEquals(tx.siteCode, dto.siteCode)
        assertEquals(tx.pumpNumber, dto.pumpNumber)
        assertEquals(tx.nozzleNumber, dto.nozzleNumber)
        assertEquals(tx.productCode, dto.productCode)
        assertEquals(tx.volumeMicrolitres, dto.volumeMicrolitres)
        assertEquals(tx.amountMinorUnits, dto.amountMinorUnits)
        assertEquals(tx.unitPriceMinorPerLitre, dto.unitPriceMinorPerLitre)
        assertEquals(tx.currencyCode, dto.currencyCode)
        assertEquals(tx.startedAt, dto.startedAt)
        assertEquals(tx.completedAt, dto.completedAt)
        assertEquals(tx.fccVendor, dto.fccVendor)
        assertEquals("lei-test-123", dto.legalEntityId)
        assertEquals(tx.status, dto.status)
        assertEquals(tx.ingestionSource, dto.ingestionSource)
        assertEquals(tx.createdAt, dto.ingestedAt) // createdAt maps to ingestedAt
        assertEquals(tx.schemaVersion, dto.schemaVersion)
        assertEquals(false, dto.isDuplicate) // always false for Edge-uploaded
        assertEquals(tx.correlationId, dto.correlationId)
    }

    @Test
    fun `upload request preserves chronological ordering (oldest first)`() = runTest {
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

        val txOld = makeTransaction(createdAt = "2024-01-01T08:00:00Z")
        val txNew = makeTransaction(createdAt = "2024-01-01T12:00:00Z")
        // Buffer returns in chronological order (oldest first)
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(txOld, txNew)

        val requestSlot = slot<CloudUploadRequest>()
        coEvery { cloudApiClient.uploadBatch(capture(requestSlot), any()) } returns CloudUploadResult.Success(
            CloudUploadResponse(
                results = listOf(
                    CloudUploadRecordResult(
                        fccTransactionId = txOld.fccTransactionId,
                        siteCode = txOld.siteCode,
                        outcome = "ACCEPTED",
                        id = UUID.randomUUID().toString(),
                    ),
                    CloudUploadRecordResult(
                        fccTransactionId = txNew.fccTransactionId,
                        siteCode = txNew.siteCode,
                        outcome = "ACCEPTED",
                        id = UUID.randomUUID().toString(),
                    ),
                ),
                acceptedCount = 2,
                duplicateCount = 0,
                rejectedCount = 0,
            ),
        )

        worker.uploadPendingBatch()

        // Verify order is preserved in the request
        val dtos = requestSlot.captured.transactions
        assertEquals(txOld.id, dtos[0].id)
        assertEquals(txNew.id, dtos[1].id)
    }

    // -------------------------------------------------------------------------
    // Cloud response parsing with unknown/missing fields
    // -------------------------------------------------------------------------

    @Test
    fun `upload response with extra unknown fields is parsed correctly`() {
        val responseJson = """
            {
                "results": [
                    {
                        "fccTransactionId": "FCC-001",
                        "siteCode": "SITE-001",
                        "outcome": "ACCEPTED",
                        "id": "cloud-uuid-123",
                        "unknownField": "should be ignored",
                        "extraNested": {"key": "value"}
                    }
                ],
                "acceptedCount": 1,
                "duplicateCount": 0,
                "rejectedCount": 0,
                "extraServerField": true
            }
        """.trimIndent()

        val response = json.decodeFromString<CloudUploadResponse>(responseJson)
        assertEquals(1, response.acceptedCount)
        assertEquals("ACCEPTED", response.results[0].outcome)
        assertEquals("cloud-uuid-123", response.results[0].id)
    }

    @Test
    fun `upload response with missing optional error field is parsed correctly`() {
        val responseJson = """
            {
                "results": [
                    {
                        "fccTransactionId": "FCC-001",
                        "siteCode": "SITE-001",
                        "outcome": "ACCEPTED"
                    }
                ],
                "acceptedCount": 1,
                "duplicateCount": 0,
                "rejectedCount": 0
            }
        """.trimIndent()

        val response = json.decodeFromString<CloudUploadResponse>(responseJson)
        assertEquals(1, response.results.size)
        assertEquals(null, response.results[0].id) // optional, null when missing
        assertEquals(null, response.results[0].error) // optional, null when missing
    }

    @Test
    fun `synced status response with unknown status values is parsed`() {
        val responseJson = """
            {
                "statuses": [
                    {"id": "fcc-1", "status": "SYNCED_TO_ODOO"},
                    {"id": "fcc-2", "status": "FUTURE_STATUS_VALUE"},
                    {"id": "fcc-3", "status": "NOT_FOUND"}
                ]
            }
        """.trimIndent()

        val response = json.decodeFromString<SyncedStatusResponse>(responseJson)
        assertEquals(3, response.statuses.size)
        // Future unknown status values are preserved as strings
        assertEquals("FUTURE_STATUS_VALUE", response.statuses[1].status)
    }

    // -------------------------------------------------------------------------
    // Config version compatibility
    // -------------------------------------------------------------------------

    @Test
    fun `config poll 304 Not Modified does not apply config`() = runTest {
        val configManager: ConfigManager = mockk(relaxed = true)
        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val cloudApiClient: CloudApiClient = mockk()
        val tokenProvider: DeviceTokenProvider = mockk()

        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns "token"
        every { configManager.currentConfigVersion } returns 5
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit
        coEvery { cloudApiClient.getConfig(5, "token") } returns CloudConfigPollResult.NotModified

        val worker = ConfigPollWorker(
            configManager = configManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )

        worker.pollConfig()

        // applyConfig should NOT be called
        coVerify(exactly = 0) { configManager.applyConfig(any(), any()) }
        // lastConfigPullAt should still be updated
        coVerify { syncStateDao.upsert(any()) }
    }

    @Test
    fun `config poll malformed JSON records failure`() = runTest {
        val configManager: ConfigManager = mockk(relaxed = true)
        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val cloudApiClient: CloudApiClient = mockk()
        val tokenProvider: DeviceTokenProvider = mockk()

        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns "token"
        every { configManager.currentConfigVersion } returns null
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit
        // Cloud returns success but with invalid JSON
        coEvery { cloudApiClient.getConfig(null, "token") } returns
            CloudConfigPollResult.Success(rawJson = "NOT VALID JSON {{{", etag = "\"6\"")

        val worker = ConfigPollWorker(
            configManager = configManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )

        worker.pollConfig()

        // Should record a failure (JSON parse error)
        assertTrue(worker.consecutiveFailureCount > 0)
        // applyConfig should NOT be called
        coVerify(exactly = 0) { configManager.applyConfig(any(), any()) }
    }

    @Test
    fun `config poll rejected config records failure`() = runTest {
        val configManager: ConfigManager = mockk(relaxed = true)
        val syncStateDao: SyncStateDao = mockk(relaxed = true)
        val cloudApiClient: CloudApiClient = mockk()
        val tokenProvider: DeviceTokenProvider = mockk()

        every { tokenProvider.isDecommissioned() } returns false
        every { tokenProvider.getAccessToken() } returns "token"
        every { configManager.currentConfigVersion } returns null
        coEvery { syncStateDao.get() } returns null
        coEvery { syncStateDao.upsert(any()) } returns Unit

        // Return valid JSON that ConfigManager rejects
        val configJson = """{"configVersion": 1, "schemaVersion": "1.0"}"""
        coEvery { cloudApiClient.getConfig(null, "token") } returns
            CloudConfigPollResult.Success(rawJson = configJson, etag = "\"1\"")
        coEvery { configManager.applyConfig(any(), any()) } returns
            ConfigApplyResult.Rejected("Incompatible schema version")

        val worker = ConfigPollWorker(
            configManager = configManager,
            syncStateDao = syncStateDao,
            cloudApiClient = cloudApiClient,
            tokenProvider = tokenProvider,
        )

        worker.pollConfig()

        assertTrue(worker.consecutiveFailureCount > 0)
    }

    // -------------------------------------------------------------------------
    // Dedup handshake — cloud confirms DUPLICATE
    // -------------------------------------------------------------------------

    @Test
    fun `DUPLICATE outcome from cloud marks record UPLOADED — no re-upload`() = runTest {
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

        val tx = makeTransaction()
        coEvery { bufferManager.getPendingBatch(any()) } returns listOf(tx)
        coEvery { cloudApiClient.uploadBatch(any(), any()) } returns CloudUploadResult.Success(
            CloudUploadResponse(
                results = listOf(
                    CloudUploadRecordResult(
                        fccTransactionId = tx.fccTransactionId,
                        siteCode = tx.siteCode,
                        outcome = "DUPLICATE",
                        id = "existing-cloud-uuid",
                    ),
                ),
                acceptedCount = 0,
                duplicateCount = 1,
                rejectedCount = 0,
            ),
        )

        worker.uploadPendingBatch()

        // DUPLICATE → UPLOADED (same as ACCEPTED per §5.3)
        coVerify { bufferManager.markUploaded(listOf(tx.id)) }
        assertEquals(0, worker.consecutiveFailureCount)
    }

    // -------------------------------------------------------------------------
    // Cloud error response parsing
    // -------------------------------------------------------------------------

    @Test
    fun `CloudErrorResponse with extra fields is parsed correctly`() {
        val errorJson = """{"errorCode": "VALIDATION_ERROR", "message": "Field missing", "extra": "ignored"}"""
        val error = json.decodeFromString<CloudErrorResponse>(errorJson)
        assertEquals("VALIDATION_ERROR", error.errorCode)
        assertEquals("Field missing", error.message)
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private fun makeTransaction(
        createdAt: String = "2024-01-01T10:01:05Z",
    ): BufferedTransaction = BufferedTransaction(
        id = UUID.randomUUID().toString(),
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
        uploadAttempts = 0,
        lastUploadAttemptAt = null,
        lastUploadError = null,
        schemaVersion = 1,
        createdAt = createdAt,
        updatedAt = createdAt,
    )
}
