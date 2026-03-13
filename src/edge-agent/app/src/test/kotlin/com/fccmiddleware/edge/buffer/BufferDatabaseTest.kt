package com.fccmiddleware.edge.buffer

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.fccmiddleware.edge.adapter.common.PreAuthStatus
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import com.fccmiddleware.edge.buffer.entity.AuditLog
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.buffer.entity.Nozzle
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import com.fccmiddleware.edge.buffer.entity.SyncState
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
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
 * BufferDatabaseTest — Room in-memory database correctness tests.
 *
 * Validates:
 *   - Unique indexes prevent duplicate inserts (IGNORE strategy)
 *   - getPendingForUpload returns records in createdAt ASC order
 *   - getForLocalApi excludes SYNCED_TO_ODOO records
 *   - Single-row tables (SyncState, AgentConfig) upsert correctly
 *   - PreAuthDao idempotency key enforced
 *   - NozzleDao replaceAll atomically replaces nozzle set
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class BufferDatabaseTest {

    private lateinit var db: BufferDatabase

    @Before
    fun setUp() {
        db = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            BufferDatabase::class.java
        ).allowMainThreadQueries().build()
    }

    @After
    fun tearDown() {
        db.close()
    }

    // -------------------------------------------------------------------------
    // TransactionBufferDao
    // -------------------------------------------------------------------------

    @Test
    fun `insert duplicate fcc_transaction_id + site_code is silently ignored`() = runBlocking {
        val dao = db.transactionDao()
        val tx = buildTransaction(fccTransactionId = "FCC-001", siteCode = "SITE_A")

        val rowId1 = dao.insert(tx)
        val rowId2 = dao.insert(tx.copy(id = UUID.randomUUID().toString())) // same dedup key

        assertTrue("First insert should succeed (rowId > 0)", rowId1 > 0)
        assertEquals("Duplicate insert should be ignored (rowId = -1)", -1L, rowId2)

        val counts = dao.countByStatus()
        val pending = counts.firstOrNull { it.syncStatus == "PENDING" }?.count ?: 0
        assertEquals("Only 1 record should exist", 1, pending)
    }

    @Test
    fun `getPendingForUpload returns records in createdAt ASC order`() = runBlocking {
        val dao = db.transactionDao()
        val base = Instant.parse("2024-01-15T10:00:00Z")

        // Insert 5 PENDING records with deliberately non-sequential insertion order
        val records = listOf(
            buildTransaction(fccTransactionId = "FCC-003", createdAt = base.plusSeconds(200).toString()),
            buildTransaction(fccTransactionId = "FCC-001", createdAt = base.toString()),
            buildTransaction(fccTransactionId = "FCC-005", createdAt = base.plusSeconds(400).toString()),
            buildTransaction(fccTransactionId = "FCC-002", createdAt = base.plusSeconds(100).toString()),
            buildTransaction(fccTransactionId = "FCC-004", createdAt = base.plusSeconds(300).toString()),
        )
        records.forEach { dao.insert(it) }

        val pending = dao.getPendingForUpload(limit = 10)

        assertEquals(5, pending.size)
        assertEquals("FCC-001", pending[0].fccTransactionId)
        assertEquals("FCC-002", pending[1].fccTransactionId)
        assertEquals("FCC-003", pending[2].fccTransactionId)
        assertEquals("FCC-004", pending[3].fccTransactionId)
        assertEquals("FCC-005", pending[4].fccTransactionId)
    }

    @Test
    fun `getForLocalApi excludes terminal-state records`() = runBlocking {
        val dao = db.transactionDao()

        dao.insert(buildTransaction(fccTransactionId = "FCC-P1", syncStatus = "PENDING"))
        dao.insert(buildTransaction(fccTransactionId = "FCC-U1", syncStatus = "UPLOADED"))
        dao.insert(buildTransaction(fccTransactionId = "FCC-S1", syncStatus = "SYNCED_TO_ODOO"))
        dao.insert(buildTransaction(fccTransactionId = "FCC-A1", syncStatus = "ARCHIVED"))
        dao.insert(buildTransaction(fccTransactionId = "FCC-D1", syncStatus = "DEAD_LETTER"))

        val results = dao.getForLocalApi(limit = 50, offset = 0)

        assertEquals(2, results.size)
        assertTrue("SYNCED_TO_ODOO must not appear", results.none { it.syncStatus == "SYNCED_TO_ODOO" })
        assertTrue("ARCHIVED must not appear", results.none { it.syncStatus == "ARCHIVED" })
        assertTrue("DEAD_LETTER must not appear", results.none { it.syncStatus == "DEAD_LETTER" })
        assertTrue("PENDING must appear", results.any { it.fccTransactionId == "FCC-P1" })
        assertTrue("UPLOADED must appear", results.any { it.fccTransactionId == "FCC-U1" })
    }

    @Test
    fun `AF-025 getByIdForLocalApi excludes terminal-state records`() = runBlocking {
        val dao = db.transactionDao()

        val pendingTx = buildTransaction(fccTransactionId = "FCC-P-ID", syncStatus = "PENDING")
        val syncedTx = buildTransaction(fccTransactionId = "FCC-S-ID", syncStatus = "SYNCED_TO_ODOO")
        val archivedTx = buildTransaction(fccTransactionId = "FCC-A-ID", syncStatus = "ARCHIVED")
        val deadLetterTx = buildTransaction(fccTransactionId = "FCC-D-ID", syncStatus = "DEAD_LETTER")

        dao.insert(pendingTx)
        dao.insert(syncedTx)
        dao.insert(archivedTx)
        dao.insert(deadLetterTx)

        // getByIdForLocalApi should return PENDING
        assertNotNull(dao.getByIdForLocalApi(pendingTx.id))
        // getByIdForLocalApi should exclude terminal states
        assertNull(dao.getByIdForLocalApi(syncedTx.id))
        assertNull(dao.getByIdForLocalApi(archivedTx.id))
        assertNull(dao.getByIdForLocalApi(deadLetterTx.id))
        // getById (unfiltered) should still return all
        assertNotNull(dao.getById(syncedTx.id))
    }

    @Test
    fun `updateSyncStatus updates all relevant fields`() = runBlocking {
        val dao = db.transactionDao()
        val tx = buildTransaction(fccTransactionId = "FCC-UPD-001")
        dao.insert(tx)

        val attemptAt = "2024-01-15T11:00:00Z"
        val now = "2024-01-15T11:00:01Z"
        dao.updateSyncStatus(
            id = tx.id,
            syncStatus = "UPLOADED",
            attempts = 1,
            lastAttemptAt = attemptAt,
            error = null,
            now = now,
        )

        val updated = dao.getById(tx.id)
        assertNotNull(updated)
        assertEquals("UPLOADED", updated!!.syncStatus)
        assertEquals(1, updated.uploadAttempts)
        assertEquals(attemptAt, updated.lastUploadAttemptAt)
        assertEquals(now, updated.updatedAt)
    }

    @Test
    fun `markSyncedToOdoo updates matching records by fccTransactionId`() = runBlocking {
        val dao = db.transactionDao()
        dao.insert(buildTransaction(fccTransactionId = "FCC-X1", syncStatus = "UPLOADED"))
        dao.insert(buildTransaction(fccTransactionId = "FCC-X2", syncStatus = "UPLOADED"))
        dao.insert(buildTransaction(fccTransactionId = "FCC-X3", syncStatus = "UPLOADED"))

        val now = Instant.now().toString()
        dao.markSyncedToOdoo(listOf("FCC-X1", "FCC-X3"), now)

        val results = dao.getForLocalApi(limit = 50, offset = 0)
        // FCC-X1 and FCC-X3 should now be SYNCED_TO_ODOO and excluded
        assertEquals(1, results.size)
        assertEquals("FCC-X2", results[0].fccTransactionId)
    }

    // -------------------------------------------------------------------------
    // PreAuthDao
    // -------------------------------------------------------------------------

    @Test
    fun `PreAuthDao insert duplicate odoo_order_id + site_code is silently ignored`() = runBlocking {
        val dao = db.preAuthDao()
        val record = buildPreAuthRecord(odooOrderId = "ORDER-001", siteCode = "SITE_A")

        val rowId1 = dao.insert(record)
        val rowId2 = dao.insert(record.copy(id = UUID.randomUUID().toString())) // same idemp key

        assertTrue("First insert should succeed", rowId1 > 0)
        assertEquals("Duplicate insert should be ignored", -1L, rowId2)
    }

    @Test
    fun `PreAuthDao getByOdooOrderId returns correct record`() = runBlocking {
        val dao = db.preAuthDao()
        val record = buildPreAuthRecord(odooOrderId = "ORDER-LOOKUP", siteCode = "SITE_B")
        dao.insert(record)

        val found = dao.getByOdooOrderId("ORDER-LOOKUP", "SITE_B")
        assertNotNull(found)
        assertEquals(record.id, found!!.id)

        val notFound = dao.getByOdooOrderId("ORDER-LOOKUP", "SITE_OTHER")
        assertNull(notFound)
    }

    @Test
    fun `PreAuthDao getUnsynced returns only unsynced records in ASC order`() = runBlocking {
        val dao = db.preAuthDao()
        val base = Instant.parse("2024-01-15T10:00:00Z")

        dao.insert(buildPreAuthRecord(odooOrderId = "O-3", isCloudSynced = 0, createdAt = base.plusSeconds(200).toString()))
        dao.insert(buildPreAuthRecord(odooOrderId = "O-1", isCloudSynced = 0, createdAt = base.toString()))
        dao.insert(buildPreAuthRecord(odooOrderId = "O-2", isCloudSynced = 1, createdAt = base.plusSeconds(100).toString()))

        val unsynced = dao.getUnsynced(limit = 10)

        assertEquals(2, unsynced.size)
        assertEquals("O-1", unsynced[0].odooOrderId)
        assertEquals("O-3", unsynced[1].odooOrderId)
    }

    @Test
    fun `PreAuthDao getUnsynced returns legacy missing-unit-price row only before first failure`() = runBlocking {
        val dao = db.preAuthDao()
        val legacy = buildPreAuthRecord(odooOrderId = "LEGACY", unitPrice = null)
        dao.insert(legacy)

        val firstFetch = dao.getUnsynced(limit = 10)
        assertEquals(listOf("LEGACY"), firstFetch.map { it.odooOrderId })

        dao.recordCloudSyncFailure(legacy.id, Instant.now().toString())

        val secondFetch = dao.getUnsynced(limit = 10)
        assertTrue(secondFetch.none { it.odooOrderId == "LEGACY" })
    }

    @Test
    fun `PreAuthDao getExpiring returns records in active states past expiry`() = runBlocking {
        val dao = db.preAuthDao()
        val pastExpiry = Instant.parse("2024-01-14T12:00:00Z").toString()
        val futureExpiry = Instant.parse("2024-01-16T12:00:00Z").toString()
        val now = Instant.parse("2024-01-15T12:00:00Z").toString()

        dao.insert(buildPreAuthRecord(odooOrderId = "EXPIRE-PENDING", status = "PENDING", expiresAt = pastExpiry))
        dao.insert(buildPreAuthRecord(odooOrderId = "EXPIRE-AUTH", status = "AUTHORIZED", expiresAt = pastExpiry))
        dao.insert(buildPreAuthRecord(odooOrderId = "FUTURE-PENDING", status = "PENDING", expiresAt = futureExpiry))
        dao.insert(buildPreAuthRecord(odooOrderId = "COMPLETED", status = "COMPLETED", expiresAt = pastExpiry))

        val expiring = dao.getExpiring(now, 50)

        assertEquals(2, expiring.size)
        assertTrue(expiring.all { it.status in listOf(PreAuthStatus.PENDING, PreAuthStatus.AUTHORIZED, PreAuthStatus.DISPENSING) })
        assertTrue(expiring.none { it.odooOrderId == "COMPLETED" })
        assertTrue(expiring.none { it.odooOrderId == "FUTURE-PENDING" })
    }

    @Test
    fun `PreAuthDao getExpiring respects limit and returns oldest-first (PA-P02)`() = runBlocking {
        val dao = db.preAuthDao()
        val t1 = "2024-01-14T10:00:00Z"
        val t2 = "2024-01-14T11:00:00Z"
        val t3 = "2024-01-14T12:00:00Z"
        val now = "2024-01-15T00:00:00Z"

        dao.insert(buildPreAuthRecord(odooOrderId = "OLD-1", status = "PENDING", expiresAt = t1))
        dao.insert(buildPreAuthRecord(odooOrderId = "OLD-2", status = "PENDING", expiresAt = t2))
        dao.insert(buildPreAuthRecord(odooOrderId = "OLD-3", status = "PENDING", expiresAt = t3))

        val limited = dao.getExpiring(now, 2)

        assertEquals(2, limited.size)
        assertEquals("OLD-1", limited[0].odooOrderId)
        assertEquals("OLD-2", limited[1].odooOrderId)
    }

    // -------------------------------------------------------------------------
    // NozzleDao
    // -------------------------------------------------------------------------

    @Test
    fun `NozzleDao replaceAll atomically replaces nozzle set`() = runBlocking {
        val dao = db.nozzleDao()
        val siteCode = "SITE_NZ"

        val initial = listOf(
            buildNozzle(id = "N1", siteCode = siteCode, odooPump = 1, odooNozzle = 1, fccPump = 10, fccNozzle = 10),
            buildNozzle(id = "N2", siteCode = siteCode, odooPump = 1, odooNozzle = 2, fccPump = 10, fccNozzle = 11),
        )
        dao.replaceAll(siteCode, initial)
        assertEquals(2, dao.getAll(siteCode).size)

        val updated = listOf(
            buildNozzle(id = "N3", siteCode = siteCode, odooPump = 2, odooNozzle = 1, fccPump = 20, fccNozzle = 10),
        )
        dao.replaceAll(siteCode, updated)
        val after = dao.getAll(siteCode)
        assertEquals(1, after.size)
        assertEquals("N3", after[0].id)
    }

    @Test
    fun `NozzleDao resolveForPreAuth returns correct mapping`() = runBlocking {
        val dao = db.nozzleDao()
        val nozzle = buildNozzle(
            siteCode = "SITE_R",
            odooPump = 3, odooNozzle = 2,
            fccPump = 30, fccNozzle = 20,
        )
        dao.replaceAll("SITE_R", listOf(nozzle))

        val found = dao.resolveForPreAuth("SITE_R", 3, 2)
        assertNotNull(found)
        assertEquals(30, found!!.fccPumpNumber)
        assertEquals(20, found.fccNozzleNumber)

        val notFound = dao.resolveForPreAuth("SITE_R", 3, 99)
        assertNull(notFound)
    }

    // -------------------------------------------------------------------------
    // SyncStateDao
    // -------------------------------------------------------------------------

    @Test
    fun `SyncStateDao returns null on first access and upserts correctly`() = runBlocking {
        val dao = db.syncStateDao()
        assertNull(dao.get())

        val now = Instant.now().toString()
        val state = SyncState(id = 1, lastFccCursor = "cursor-abc", lastUploadAt = now, updatedAt = now)
        dao.upsert(state)

        val loaded = dao.get()
        assertNotNull(loaded)
        assertEquals("cursor-abc", loaded!!.lastFccCursor)
    }

    @Test
    fun `SyncStateDao upsert replaces existing row`() = runBlocking {
        val dao = db.syncStateDao()
        val now = Instant.now().toString()

        dao.upsert(SyncState(id = 1, lastFccCursor = "cursor-v1", updatedAt = now))
        dao.upsert(SyncState(id = 1, lastFccCursor = "cursor-v2", updatedAt = now))

        val loaded = dao.get()
        assertEquals("cursor-v2", loaded!!.lastFccCursor)
    }

    // -------------------------------------------------------------------------
    // AgentConfigDao
    // -------------------------------------------------------------------------

    @Test
    fun `AgentConfigDao returns null on first access and upserts correctly`() = runBlocking {
        val dao = db.agentConfigDao()
        assertNull(dao.get())

        val config = AgentConfig(
            id = 1,
            configJson = """{"site_code":"SITE_001"}""",
            configVersion = 1,
            schemaVersion = 1,
            receivedAt = Instant.now().toString(),
        )
        dao.upsert(config)

        val loaded = dao.get()
        assertNotNull(loaded)
        assertEquals(1, loaded!!.configVersion)
    }

    // -------------------------------------------------------------------------
    // AuditLogDao
    // -------------------------------------------------------------------------

    @Test
    fun `AuditLogDao inserts and retrieves entries in reverse-chronological order`() = runBlocking {
        val dao = db.auditLogDao()

        dao.insert(AuditLog(eventType = "UPLOAD_START", message = "msg1", correlationId = null, createdAt = "2024-01-15T10:00:00Z"))
        dao.insert(AuditLog(eventType = "UPLOAD_OK", message = "msg2", correlationId = null, createdAt = "2024-01-15T10:01:00Z"))
        dao.insert(AuditLog(eventType = "FCC_POLL", message = "msg3", correlationId = null, createdAt = "2024-01-15T10:02:00Z"))

        val recent = dao.getRecent(limit = 2)
        assertEquals(2, recent.size)
        assertEquals("FCC_POLL", recent[0].eventType)
        assertEquals("UPLOAD_OK", recent[1].eventType)
    }

    // -------------------------------------------------------------------------
    // Builder helpers
    // -------------------------------------------------------------------------

    private fun buildTransaction(
        id: String = UUID.randomUUID().toString(),
        fccTransactionId: String = UUID.randomUUID().toString(),
        siteCode: String = "SITE_A",
        syncStatus: String = "PENDING",
        createdAt: String = Instant.now().toString(),
        completedAt: String = Instant.now().toString(),
    ) = BufferedTransaction(
        id = id,
        fccTransactionId = fccTransactionId,
        siteCode = siteCode,
        pumpNumber = 1,
        nozzleNumber = 1,
        productCode = "ULP95",
        volumeMicrolitres = 50_000_000L,
        amountMinorUnits = 75_000L,
        unitPriceMinorPerLitre = 1_500L,
        currencyCode = "MWK",
        startedAt = createdAt,
        completedAt = completedAt,
        fiscalReceiptNumber = null,
        fccVendor = "DOMS",
        attendantId = null,
        status = "PENDING",
        syncStatus = syncStatus,
        ingestionSource = "RELAY",
        rawPayloadJson = null,
        correlationId = UUID.randomUUID().toString(),
        uploadAttempts = 0,
        lastUploadAttemptAt = null,
        lastUploadError = null,
        schemaVersion = 1,
        createdAt = createdAt,
        updatedAt = createdAt,
    )

    private fun buildPreAuthRecord(
        id: String = UUID.randomUUID().toString(),
        odooOrderId: String = UUID.randomUUID().toString(),
        siteCode: String = "SITE_A",
        unitPrice: Long? = 1_500L,
        status: String = "PENDING",
        isCloudSynced: Int = 0,
        expiresAt: String = Instant.now().plusSeconds(300).toString(),
        createdAt: String = Instant.now().toString(),
    ) = PreAuthRecord(
        id = id,
        siteCode = siteCode,
        odooOrderId = odooOrderId,
        pumpNumber = 1,
        nozzleNumber = 1,
        productCode = "ULP95",
        currencyCode = "MWK",
        requestedAmountMinorUnits = 50_000L,
        unitPrice = unitPrice,
        authorizedAmountMinorUnits = null,
        status = PreAuthStatus.valueOf(status),
        fccCorrelationId = null,
        fccAuthorizationCode = null,
        failureReason = null,
        customerName = null,
        customerTaxId = null,
        rawFccResponse = null,
        requestedAt = createdAt,
        authorizedAt = null,
        completedAt = null,
        expiresAt = expiresAt,
        isCloudSynced = isCloudSynced,
        cloudSyncAttempts = 0,
        lastCloudSyncAttemptAt = null,
        schemaVersion = 1,
        createdAt = createdAt,
    )

    private fun buildNozzle(
        id: String = UUID.randomUUID().toString(),
        siteCode: String = "SITE_A",
        odooPump: Int = 1,
        odooNozzle: Int = 1,
        fccPump: Int = 10,
        fccNozzle: Int = 10,
    ) = Nozzle(
        id = id,
        siteCode = siteCode,
        odooPumpNumber = odooPump,
        fccPumpNumber = fccPump,
        odooNozzleNumber = odooNozzle,
        fccNozzleNumber = fccNozzle,
        productCode = "ULP95",
        isActive = 1,
        syncedAt = Instant.now().toString(),
        createdAt = Instant.now().toString(),
        updatedAt = Instant.now().toString(),
    )
}
