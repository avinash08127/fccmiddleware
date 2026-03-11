package com.fccmiddleware.edge.buffer

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.fccmiddleware.edge.buffer.entity.AuditLog
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
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
 * CleanupWorkerTest — verifies retention cleanup across all three tables.
 *
 * Uses an in-memory Room database (Robolectric). All tests insert records at
 * controlled timestamps to exercise the cutoff boundary precisely.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class CleanupWorkerTest {

    private lateinit var db: BufferDatabase
    private lateinit var worker: CleanupWorker

    @Before
    fun setUp() {
        db = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            BufferDatabase::class.java
        ).allowMainThreadQueries().build()
        worker = CleanupWorker(db.transactionDao(), db.preAuthDao(), db.auditLogDao())
    }

    @After
    fun tearDown() {
        db.close()
    }

    // -------------------------------------------------------------------------
    // Transaction cleanup
    // -------------------------------------------------------------------------

    @Test
    fun `runCleanup deletes SYNCED_TO_ODOO transactions older than retentionDays`() = runBlocking {
        val old = Instant.now().minus(10, ChronoUnit.DAYS).toString()
        val fresh = Instant.now().minus(1, ChronoUnit.DAYS).toString()

        db.transactionDao().apply {
            insert(buildTx(fccId = "OLD-SYNCED", syncStatus = "SYNCED_TO_ODOO", updatedAt = old))
            insert(buildTx(fccId = "FRESH-SYNCED", syncStatus = "SYNCED_TO_ODOO", updatedAt = fresh))
            insert(buildTx(fccId = "OLD-PENDING", syncStatus = "PENDING", updatedAt = old))
        }

        val result = worker.runCleanup(retentionDays = 7)

        assertEquals("Only 1 old SYNCED_TO_ODOO should be deleted", 1, result.transactionsDeleted)

        val counts = db.transactionDao().countByStatus()
        val syncedCount = counts.firstOrNull { it.syncStatus == "SYNCED_TO_ODOO" }?.count ?: 0
        assertEquals("Fresh SYNCED_TO_ODOO should remain", 1, syncedCount)
        val pendingCount = counts.firstOrNull { it.syncStatus == "PENDING" }?.count ?: 0
        assertEquals("PENDING records must not be deleted", 1, pendingCount)
    }

    @Test
    fun `runCleanup does not delete SYNCED_TO_ODOO records within retentionDays`() = runBlocking {
        val fresh = Instant.now().minus(2, ChronoUnit.DAYS).toString()
        db.transactionDao().insert(
            buildTx(fccId = "FRESH-SYNCED", syncStatus = "SYNCED_TO_ODOO", updatedAt = fresh)
        )

        val result = worker.runCleanup(retentionDays = 7)

        assertEquals(0, result.transactionsDeleted)
    }

    @Test
    fun `runCleanup does not delete PENDING or UPLOADED transactions regardless of age`() = runBlocking {
        val veryOld = Instant.now().minus(90, ChronoUnit.DAYS).toString()
        db.transactionDao().apply {
            insert(buildTx(fccId = "OLD-PENDING", syncStatus = "PENDING", updatedAt = veryOld))
            insert(buildTx(fccId = "OLD-UPLOADED", syncStatus = "UPLOADED", updatedAt = veryOld))
        }

        val result = worker.runCleanup(retentionDays = 7)

        assertEquals(0, result.transactionsDeleted)
    }

    // -------------------------------------------------------------------------
    // Pre-auth cleanup
    // -------------------------------------------------------------------------

    @Test
    fun `runCleanup deletes terminal pre-auth records older than retentionDays`() = runBlocking {
        val old = Instant.now().minus(10, ChronoUnit.DAYS).toString()
        val fresh = Instant.now().minus(1, ChronoUnit.DAYS).toString()

        db.preAuthDao().apply {
            insert(buildPreAuth(orderId = "OLD-COMPLETED", status = "COMPLETED", createdAt = old))
            insert(buildPreAuth(orderId = "OLD-EXPIRED", status = "EXPIRED", createdAt = old))
            insert(buildPreAuth(orderId = "OLD-CANCELLED", status = "CANCELLED", createdAt = old))
            insert(buildPreAuth(orderId = "OLD-FAILED", status = "FAILED", createdAt = old))
            insert(buildPreAuth(orderId = "FRESH-COMPLETED", status = "COMPLETED", createdAt = fresh))
            insert(buildPreAuth(orderId = "OLD-PENDING", status = "PENDING", createdAt = old))
        }

        val result = worker.runCleanup(retentionDays = 7)

        // 4 old terminal records deleted; fresh COMPLETED and old PENDING survive
        assertEquals(4, result.preAuthsDeleted)

        val remaining = db.preAuthDao().getByOdooOrderId("FRESH-COMPLETED", "SITE_A")
        assertTrue("Fresh completed pre-auth must survive", remaining != null)

        val activeRemaining = db.preAuthDao().getByOdooOrderId("OLD-PENDING", "SITE_A")
        assertTrue("Old PENDING pre-auth must NOT be deleted", activeRemaining != null)
    }

    @Test
    fun `runCleanup does not delete active pre-auth records regardless of age`() = runBlocking {
        val veryOld = Instant.now().minus(60, ChronoUnit.DAYS).toString()
        db.preAuthDao().apply {
            insert(buildPreAuth(orderId = "OLD-PENDING", status = "PENDING", createdAt = veryOld))
            insert(buildPreAuth(orderId = "OLD-AUTHORIZED", status = "AUTHORIZED", createdAt = veryOld))
            insert(buildPreAuth(orderId = "OLD-DISPENSING", status = "DISPENSING", createdAt = veryOld))
        }

        val result = worker.runCleanup(retentionDays = 7)

        assertEquals(0, result.preAuthsDeleted)
    }

    // -------------------------------------------------------------------------
    // Audit log cleanup
    // -------------------------------------------------------------------------

    @Test
    fun `runCleanup trims audit log entries older than retentionDays`() = runBlocking {
        val old = Instant.now().minus(10, ChronoUnit.DAYS).toString()
        val fresh = Instant.now().minus(1, ChronoUnit.DAYS).toString()

        db.auditLogDao().apply {
            insert(AuditLog(eventType = "OLD_EVENT_1", message = "old", correlationId = null, createdAt = old))
            insert(AuditLog(eventType = "OLD_EVENT_2", message = "old", correlationId = null, createdAt = old))
            insert(AuditLog(eventType = "FRESH_EVENT", message = "fresh", correlationId = null, createdAt = fresh))
        }

        val result = worker.runCleanup(retentionDays = 7)

        // 2 old entries + CLEANUP_RUN entry itself is fresh, so only old 2 should be deleted
        // (the CLEANUP_RUN audit entry is inserted AFTER deleteOlderThan, so it's fresh)
        assertEquals(2, result.auditEntriesDeleted)

        val remaining = db.auditLogDao().getRecent(100)
        assertTrue("Fresh event must remain", remaining.any { it.eventType == "FRESH_EVENT" })
        assertTrue("Old events must be deleted", remaining.none { it.eventType == "OLD_EVENT_1" })
    }

    @Test
    fun `runCleanup does not delete audit entries within retentionDays`() = runBlocking {
        val fresh = Instant.now().minus(3, ChronoUnit.DAYS).toString()
        db.auditLogDao().insert(
            AuditLog(eventType = "FRESH_AUDIT", message = "msg", correlationId = null, createdAt = fresh)
        )

        val result = worker.runCleanup(retentionDays = 7)

        assertEquals(0, result.auditEntriesDeleted)
    }

    // -------------------------------------------------------------------------
    // Return value / CLEANUP_RUN audit entry
    // -------------------------------------------------------------------------

    @Test
    fun `runCleanup returns correct row counts across all tables`() = runBlocking {
        val old = Instant.now().minus(15, ChronoUnit.DAYS).toString()

        db.transactionDao().apply {
            insert(buildTx(fccId = "TX-1", syncStatus = "SYNCED_TO_ODOO", updatedAt = old))
            insert(buildTx(fccId = "TX-2", syncStatus = "SYNCED_TO_ODOO", updatedAt = old))
        }
        db.preAuthDao().insert(buildPreAuth(orderId = "PA-1", status = "COMPLETED", createdAt = old))
        db.auditLogDao().insert(
            AuditLog(eventType = "EVT", message = "m", correlationId = null, createdAt = old)
        )

        val result = worker.runCleanup(retentionDays = 7)

        assertEquals(2, result.transactionsDeleted)
        assertEquals(1, result.preAuthsDeleted)
        assertEquals(1, result.auditEntriesDeleted)
    }

    @Test
    fun `runCleanup writes a CLEANUP_RUN audit log entry`() = runBlocking {
        worker.runCleanup(retentionDays = 7)

        val recent = db.auditLogDao().getRecent(10)
        assertTrue("CLEANUP_RUN event must be logged", recent.any { it.eventType == "CLEANUP_RUN" })
    }

    // -------------------------------------------------------------------------
    // Builders
    // -------------------------------------------------------------------------

    private fun buildTx(
        fccId: String,
        syncStatus: String = "PENDING",
        updatedAt: String = Instant.now().toString(),
        createdAt: String = updatedAt,
    ) = BufferedTransaction(
        id = UUID.randomUUID().toString(),
        fccTransactionId = fccId,
        siteCode = "SITE_A",
        pumpNumber = 1,
        nozzleNumber = 1,
        productCode = "ULP95",
        volumeMicrolitres = 50_000_000L,
        amountMinorUnits = 75_000L,
        unitPriceMinorPerLitre = 1_500L,
        currencyCode = "MWK",
        startedAt = createdAt,
        completedAt = createdAt,
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
        updatedAt = updatedAt,
    )

    private fun buildPreAuth(
        orderId: String,
        status: String = "PENDING",
        createdAt: String = Instant.now().toString(),
    ) = PreAuthRecord(
        id = UUID.randomUUID().toString(),
        siteCode = "SITE_A",
        odooOrderId = orderId,
        pumpNumber = 1,
        nozzleNumber = 1,
        productCode = "ULP95",
        currencyCode = "MWK",
        requestedAmountMinorUnits = 50_000L,
        authorizedAmountMinorUnits = null,
        status = status,
        fccCorrelationId = null,
        fccAuthorizationCode = null,
        failureReason = null,
        customerName = null,
        customerTaxId = null,
        rawFccResponse = null,
        requestedAt = createdAt,
        authorizedAt = null,
        completedAt = null,
        expiresAt = Instant.now().plusSeconds(300).toString(),
        isCloudSynced = 0,
        cloudSyncAttempts = 0,
        lastCloudSyncAttemptAt = null,
        schemaVersion = 1,
        createdAt = createdAt,
    )
}
