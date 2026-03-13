package com.fccmiddleware.edge.buffer

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.fccmiddleware.edge.adapter.common.FccVendor
import com.fccmiddleware.edge.adapter.common.CanonicalTransaction
import com.fccmiddleware.edge.adapter.common.IngestionSource
import com.fccmiddleware.edge.adapter.common.SyncStatus
import com.fccmiddleware.edge.adapter.common.TransactionStatus
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.security.KeystoreBackedStringCipher
import com.fccmiddleware.edge.security.KeystoreManager
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.time.Instant
import java.util.UUID

/**
 * TransactionBufferManagerTest — verifies buffer management logic on top of Room DAOs.
 *
 * All tests use an in-memory Room database (Robolectric) per the EA-2.2 spec.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class TransactionBufferManagerTest {

    private lateinit var db: BufferDatabase
    private lateinit var manager: TransactionBufferManager

    @Before
    fun setUp() {
        db = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            BufferDatabase::class.java
        ).allowMainThreadQueries().build()
        manager = TransactionBufferManager(db.transactionDao())
    }

    @After
    fun tearDown() {
        db.close()
    }

    // -------------------------------------------------------------------------
    // bufferTransaction
    // -------------------------------------------------------------------------

    @Test
    fun `bufferTransaction returns true for a new transaction`() = runBlocking {
        val tx = buildCanonical(fccTransactionId = "FCC-NEW-001")
        val result = manager.bufferTransaction(tx)
        assertTrue("New transaction should be buffered (true)", result)
    }

    @Test
    fun `bufferTransaction returns false for duplicate fccTransactionId and siteCode`() = runBlocking {
        val tx = buildCanonical(fccTransactionId = "FCC-DUP-001", siteCode = "SITE_A")
        manager.bufferTransaction(tx)

        val duplicate = tx.copy(id = UUID.randomUUID().toString()) // new id, same dedup key
        val result = manager.bufferTransaction(duplicate)

        assertFalse("Duplicate fccTransactionId+siteCode must be silently ignored (false)", result)
    }

    @Test
    fun `bufferTransaction stores syncStatus PENDING`() = runBlocking {
        val tx = buildCanonical(fccTransactionId = "FCC-PENDING-001")
        manager.bufferTransaction(tx)

        val dao = db.transactionDao()
        val stored = dao.getById(tx.id)
        assertEquals(SyncStatus.PENDING.name, stored!!.syncStatus)
    }

    @Test
    fun `bufferTransaction maps all CanonicalTransaction fields correctly`() = runBlocking {
        val tx = buildCanonical(
            id = "test-uuid-001",
            fccTransactionId = "FCC-MAP-001",
            siteCode = "SITE_ZM",
            pumpNumber = 3,
            nozzleNumber = 2,
            productCode = "AGO",
            volumeMicrolitres = 30_000_000L,
            amountMinorUnits = 45_000L,
            unitPriceMinorPerLitre = 1_500L,
        )
        manager.bufferTransaction(tx)

        val stored = db.transactionDao().getById("test-uuid-001")!!
        assertEquals("FCC-MAP-001", stored.fccTransactionId)
        assertEquals("SITE_ZM", stored.siteCode)
        assertEquals(3, stored.pumpNumber)
        assertEquals(2, stored.nozzleNumber)
        assertEquals("AGO", stored.productCode)
        assertEquals(30_000_000L, stored.volumeMicrolitres)
        assertEquals(45_000L, stored.amountMinorUnits)
        assertEquals(1_500L, stored.unitPriceMinorPerLitre)
    }

    // -------------------------------------------------------------------------
    // getPendingBatch
    // -------------------------------------------------------------------------

    @Test
    fun `getPendingBatch returns records in createdAt ASC order`() = runBlocking {
        val base = Instant.parse("2024-06-01T08:00:00Z")
        listOf("FCC-003" to 200L, "FCC-001" to 0L, "FCC-002" to 100L).forEach { (id, offset) ->
            manager.bufferTransaction(buildCanonical(fccTransactionId = id, ingestedAt = base.plusSeconds(offset).toString()))
        }

        val batch = manager.getPendingBatch(10)

        assertEquals(3, batch.size)
        assertEquals("FCC-001", batch[0].fccTransactionId)
        assertEquals("FCC-002", batch[1].fccTransactionId)
        assertEquals("FCC-003", batch[2].fccTransactionId)
    }

    @Test
    fun `getPendingBatch respects batchSize limit`() = runBlocking {
        repeat(5) { i ->
            manager.bufferTransaction(buildCanonical(fccTransactionId = "FCC-LIMIT-$i"))
        }
        val batch = manager.getPendingBatch(3)
        assertEquals(3, batch.size)
    }

    @Test
    fun `getPendingBatch does not include UPLOADED or SYNCED_TO_ODOO records`() = runBlocking {
        // Insert one PENDING and one UPLOADED directly via DAO
        val dao = db.transactionDao()
        dao.insert(buildEntity(fccTransactionId = "FCC-P", syncStatus = "PENDING"))
        dao.insert(buildEntity(fccTransactionId = "FCC-U", syncStatus = "UPLOADED"))
        dao.insert(buildEntity(fccTransactionId = "FCC-S", syncStatus = "SYNCED_TO_ODOO"))

        val batch = manager.getPendingBatch(10)

        assertEquals(1, batch.size)
        assertEquals("FCC-P", batch[0].fccTransactionId)
    }

    @Test
    fun `bufferTransaction encrypts raw payload and getPendingBatch decrypts it`() = runBlocking {
        val keystoreManager = mockRawPayloadKeystore()
        val secureManager = TransactionBufferManager(db.transactionDao(), keystoreManager = keystoreManager)
        val rawPayload = """{"vendor":"RADIX","ackCode":0}"""
        val tx = buildCanonical(fccTransactionId = "FCC-RAW-001").copy(rawPayloadJson = rawPayload)

        secureManager.bufferTransaction(tx)

        val stored = db.transactionDao().getById(tx.id)!!
        assertNotEquals(rawPayload, stored.rawPayloadJson)
        assertTrue(stored.rawPayloadJson!!.startsWith(KeystoreBackedStringCipher.ENCRYPTED_PREFIX_V1))

        val batch = secureManager.getPendingBatch(10)
        assertEquals(rawPayload, batch.single { it.id == tx.id }.rawPayloadJson)
    }

    @Test
    fun `migrateLegacyRawPayloads encrypts existing plaintext rows`() = runBlocking {
        val keystoreManager = mockRawPayloadKeystore()
        val secureManager = TransactionBufferManager(db.transactionDao(), keystoreManager = keystoreManager)
        val rawPayload = """{"vendor":"DOMS","bufferIndex":7}"""
        val row = buildEntity(fccTransactionId = "FCC-LEGACY-RAW", rawPayloadJson = rawPayload)
        db.transactionDao().insert(row)

        val migrated = secureManager.migrateLegacyRawPayloads()
        val stored = db.transactionDao().getById(row.id)!!

        assertEquals(1, migrated)
        assertNotEquals(rawPayload, stored.rawPayloadJson)
        assertTrue(stored.rawPayloadJson!!.startsWith(KeystoreBackedStringCipher.ENCRYPTED_PREFIX_V1))
        assertEquals(rawPayload, secureManager.getPendingBatch(10).single { it.id == row.id }.rawPayloadJson)
    }

    // -------------------------------------------------------------------------
    // markUploaded
    // -------------------------------------------------------------------------

    @Test
    fun `markUploaded sets syncStatus to UPLOADED`() = runBlocking {
        val tx = buildCanonical(fccTransactionId = "FCC-UPL-001")
        manager.bufferTransaction(tx)

        manager.markUploaded(listOf(tx.id))

        val stored = db.transactionDao().getById(tx.id)!!
        assertEquals(SyncStatus.UPLOADED.name, stored.syncStatus)
    }

    @Test
    fun `markUploaded handles empty list without error`() = runBlocking {
        manager.markUploaded(emptyList()) // must not throw
    }

    @Test
    fun `markUploaded batch updates multiple records`() = runBlocking {
        val ids = (1..3).map { i ->
            buildCanonical(fccTransactionId = "FCC-BULK-$i").also { manager.bufferTransaction(it) }.id
        }

        manager.markUploaded(ids)

        val dao = db.transactionDao()
        ids.forEach { id ->
            assertEquals(SyncStatus.UPLOADED.name, dao.getById(id)!!.syncStatus)
        }
    }

    // -------------------------------------------------------------------------
    // markDuplicateConfirmed
    // -------------------------------------------------------------------------

    @Test
    fun `markDuplicateConfirmed sets syncStatus to UPLOADED per state machine spec`() = runBlocking {
        val tx = buildCanonical(fccTransactionId = "FCC-DUP-CONF-001")
        manager.bufferTransaction(tx)

        manager.markDuplicateConfirmed(listOf(tx.id))

        // §5.3: if cloud returned dedup-skipped, still mark UPLOADED
        val stored = db.transactionDao().getById(tx.id)!!
        assertEquals(SyncStatus.UPLOADED.name, stored.syncStatus)
    }

    // -------------------------------------------------------------------------
    // markSyncedToOdoo
    // -------------------------------------------------------------------------

    @Test
    fun `markSyncedToOdoo sets syncStatus to SYNCED_TO_ODOO by fccTransactionId`() = runBlocking {
        val dao = db.transactionDao()
        dao.insert(buildEntity(fccTransactionId = "FCC-SYNC-1", syncStatus = "UPLOADED"))
        dao.insert(buildEntity(fccTransactionId = "FCC-SYNC-2", syncStatus = "UPLOADED"))
        dao.insert(buildEntity(fccTransactionId = "FCC-SYNC-3", syncStatus = "UPLOADED"))

        manager.markSyncedToOdoo(listOf("FCC-SYNC-1", "FCC-SYNC-3"))

        val remaining = dao.getForLocalApi(limit = 50, offset = 0)
        // Only FCC-SYNC-2 should remain visible (SYNCED_TO_ODOO excluded from local API)
        assertEquals(1, remaining.size)
        assertEquals("FCC-SYNC-2", remaining[0].fccTransactionId)
    }

    @Test
    fun `markSyncedToOdoo handles empty list without error`() = runBlocking {
        manager.markSyncedToOdoo(emptyList()) // must not throw
    }

    // -------------------------------------------------------------------------
    // getForLocalApi
    // -------------------------------------------------------------------------

    @Test
    fun `getForLocalApi excludes SYNCED_TO_ODOO records`() = runBlocking {
        val dao = db.transactionDao()
        dao.insert(buildEntity(fccTransactionId = "LA-PENDING", syncStatus = "PENDING"))
        dao.insert(buildEntity(fccTransactionId = "LA-UPLOADED", syncStatus = "UPLOADED"))
        dao.insert(buildEntity(fccTransactionId = "LA-SYNCED", syncStatus = "SYNCED_TO_ODOO"))

        val results = manager.getForLocalApi(pumpNumber = null, limit = 50, offset = 0)

        assertEquals(2, results.size)
        assertTrue(results.none { it.fccTransactionId == "LA-SYNCED" })
        assertTrue(results.any { it.fccTransactionId == "LA-PENDING" })
        assertTrue(results.any { it.fccTransactionId == "LA-UPLOADED" })
    }

    @Test
    fun `getForLocalApi with pumpNumber filters by pump`() = runBlocking {
        val dao = db.transactionDao()
        dao.insert(buildEntity(fccTransactionId = "PUMP-1-TX", pumpNumber = 1))
        dao.insert(buildEntity(fccTransactionId = "PUMP-2-TX", pumpNumber = 2))
        dao.insert(buildEntity(fccTransactionId = "PUMP-1-TX2", pumpNumber = 1))

        val pump1Results = manager.getForLocalApi(pumpNumber = 1, limit = 50, offset = 0)

        assertEquals(2, pump1Results.size)
        assertTrue(pump1Results.all { it.pumpNumber == 1 })
    }

    @Test
    fun `getForLocalApi respects limit and offset`() = runBlocking {
        repeat(5) { i ->
            db.transactionDao().insert(buildEntity(fccTransactionId = "PAGE-TX-$i"))
        }

        val page1 = manager.getForLocalApi(pumpNumber = null, limit = 2, offset = 0)
        val page2 = manager.getForLocalApi(pumpNumber = null, limit = 2, offset = 2)

        assertEquals(2, page1.size)
        assertEquals(2, page2.size)
        // Pages must not overlap
        assertTrue(page1.none { p1 -> page2.any { p2 -> p1.id == p2.id } })
    }

    // -------------------------------------------------------------------------
    // getBufferStats
    // -------------------------------------------------------------------------

    @Test
    fun `getBufferStats returns per-status counts`() = runBlocking {
        val dao = db.transactionDao()
        dao.insert(buildEntity(fccTransactionId = "STAT-P1", syncStatus = "PENDING"))
        dao.insert(buildEntity(fccTransactionId = "STAT-P2", syncStatus = "PENDING"))
        dao.insert(buildEntity(fccTransactionId = "STAT-U1", syncStatus = "UPLOADED"))
        dao.insert(buildEntity(fccTransactionId = "STAT-S1", syncStatus = "SYNCED_TO_ODOO"))

        val stats = manager.getBufferStats()

        assertEquals(2, stats[SyncStatus.PENDING])
        assertEquals(1, stats[SyncStatus.UPLOADED])
        assertEquals(1, stats[SyncStatus.SYNCED_TO_ODOO])
        assertEquals(null, stats[SyncStatus.ARCHIVED]) // not present in DB
    }

    @Test
    fun `getBufferStats returns empty map when buffer is empty`() = runBlocking {
        val stats = manager.getBufferStats()
        assertTrue(stats.isEmpty())
    }

    // -------------------------------------------------------------------------
    // Builders
    // -------------------------------------------------------------------------

    private fun buildCanonical(
        id: String = UUID.randomUUID().toString(),
        fccTransactionId: String = UUID.randomUUID().toString(),
        siteCode: String = "SITE_A",
        pumpNumber: Int = 1,
        nozzleNumber: Int = 1,
        productCode: String = "ULP95",
        volumeMicrolitres: Long = 50_000_000L,
        amountMinorUnits: Long = 75_000L,
        unitPriceMinorPerLitre: Long = 1_500L,
        ingestedAt: String = Instant.now().toString(),
    ) = CanonicalTransaction(
        id = id,
        fccTransactionId = fccTransactionId,
        siteCode = siteCode,
        pumpNumber = pumpNumber,
        nozzleNumber = nozzleNumber,
        productCode = productCode,
        volumeMicrolitres = volumeMicrolitres,
        amountMinorUnits = amountMinorUnits,
        unitPriceMinorPerLitre = unitPriceMinorPerLitre,
        currencyCode = "MWK",
        startedAt = ingestedAt,
        completedAt = ingestedAt,
        fccVendor = FccVendor.DOMS,
        legalEntityId = UUID.randomUUID().toString(),
        status = TransactionStatus.PENDING,
        ingestionSource = IngestionSource.EDGE_UPLOAD,
        ingestedAt = ingestedAt,
        updatedAt = ingestedAt,
        schemaVersion = 1,
        isDuplicate = false,
        correlationId = UUID.randomUUID().toString(),
        rawPayloadJson = null,
    )

    private fun buildEntity(
        id: String = UUID.randomUUID().toString(),
        fccTransactionId: String = UUID.randomUUID().toString(),
        siteCode: String = "SITE_A",
        syncStatus: String = "PENDING",
        pumpNumber: Int = 1,
        createdAt: String = Instant.now().toString(),
        completedAt: String = Instant.now().toString(),
        rawPayloadJson: String? = null,
    ) = BufferedTransaction(
        id = id,
        fccTransactionId = fccTransactionId,
        siteCode = siteCode,
        pumpNumber = pumpNumber,
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
        rawPayloadJson = rawPayloadJson,
        correlationId = UUID.randomUUID().toString(),
        uploadAttempts = 0,
        lastUploadAttemptAt = null,
        lastUploadError = null,
        schemaVersion = 1,
        createdAt = createdAt,
        updatedAt = createdAt,
    )

    private fun mockRawPayloadKeystore(): KeystoreManager {
        val keystoreManager = mockk<KeystoreManager>()
        every {
            keystoreManager.storeSecret(KeystoreManager.ALIAS_BUFFER_RAW_PAYLOAD, any())
        } answers {
            secondArg<String>().toByteArray(Charsets.UTF_8)
        }
        every {
            keystoreManager.retrieveSecret(KeystoreManager.ALIAS_BUFFER_RAW_PAYLOAD, any())
        } answers {
            String(secondArg<ByteArray>(), Charsets.UTF_8)
        }
        return keystoreManager
    }
}
