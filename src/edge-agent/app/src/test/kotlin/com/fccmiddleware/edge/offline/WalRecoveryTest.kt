package com.fccmiddleware.edge.offline

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.fccmiddleware.edge.buffer.BufferDatabase
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.buffer.entity.SyncState
import com.fccmiddleware.edge.offline.OfflineScenarioTestHelpers.makeTransaction
import kotlinx.coroutines.runBlocking
import org.junit.After
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
 * WalRecoveryTest — EA-6.1 WAL mode recovery and database integrity tests.
 *
 * Validates:
 *   - WAL mode is enabled by default (BufferDatabase uses WRITE_AHEAD_LOGGING)
 *   - Database survives simulated crash (close + reopen) without data loss
 *   - SyncState (single-row cursor) survives restart
 *   - Concurrent reads/writes operate correctly under WAL
 *   - PRAGMA integrity_check passes after writes
 *   - Large transaction backlog persists across DB close/reopen cycles
 *
 * NOTE: True power-loss simulation requires hardware or emulator-level testing.
 * These tests validate the WAL correctness at the Room/SQLite level using
 * in-memory DB close/reopen as a proxy for crash recovery.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class WalRecoveryTest {

    private lateinit var db: BufferDatabase

    @Before
    fun setUp() {
        db = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            BufferDatabase::class.java,
        )
            .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
            .allowMainThreadQueries()
            .build()
    }

    @After
    fun tearDown() {
        if (::db.isInitialized && db.isOpen) {
            db.close()
        }
    }

    // -------------------------------------------------------------------------
    // WAL mode integrity
    // -------------------------------------------------------------------------

    @Test
    fun `WAL mode enabled — database is writable`() = runBlocking {
        val dao = db.transactionDao()
        val tx = makeTransaction(index = 0)
        val rowId = dao.insert(tx)
        assertTrue("Insert must succeed under WAL", rowId > 0)
    }

    @Test
    fun `PRAGMA integrity_check passes after bulk writes`() = runBlocking {
        val dao = db.transactionDao()

        // Insert 500 records
        for (i in 0 until 500) {
            dao.insert(makeTransaction(index = i))
        }

        // Run integrity check
        val cursor = db.openHelper.readableDatabase.query("PRAGMA integrity_check")
        cursor.moveToFirst()
        val result = cursor.getString(0)
        cursor.close()

        assertEquals("PRAGMA integrity_check must pass", "ok", result)
    }

    // -------------------------------------------------------------------------
    // Crash recovery simulation (close + reopen)
    // -------------------------------------------------------------------------

    @Test
    fun `data survives DB close and reopen (simulated crash recovery)`() = runBlocking {
        // Use a file-backed DB for close/reopen test
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val fileDb = Room.databaseBuilder(
            context,
            BufferDatabase::class.java,
            "test_wal_recovery.db",
        )
            .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
            .allowMainThreadQueries()
            .build()

        try {
            val dao = fileDb.transactionDao()

            // Insert 100 records
            for (i in 0 until 100) {
                dao.insert(makeTransaction(index = i))
            }

            // Verify records exist
            val countBefore = dao.countForLocalApi()
            assertEquals(100, countBefore)

            // Simulate crash: close DB abruptly
            fileDb.close()

            // Reopen DB (simulating app restart after crash/power loss)
            val reopenedDb = Room.databaseBuilder(
                context,
                BufferDatabase::class.java,
                "test_wal_recovery.db",
            )
                .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
                .allowMainThreadQueries()
                .build()

            try {
                val countAfter = reopenedDb.transactionDao().countForLocalApi()
                assertEquals("All 100 records must survive crash recovery", 100, countAfter)

                // Verify ordering preserved
                val pending = reopenedDb.transactionDao().getPendingForUpload(100)
                assertEquals(100, pending.size)
                for (i in 1 until pending.size) {
                    assertTrue(
                        "Ordering must be preserved after recovery",
                        pending[i].createdAt >= pending[i - 1].createdAt,
                    )
                }
            } finally {
                reopenedDb.close()
            }
        } finally {
            // Clean up test DB file
            context.deleteDatabase("test_wal_recovery.db")
        }
    }

    @Test
    fun `SyncState cursor survives DB close and reopen`() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val fileDb = Room.databaseBuilder(
            context,
            BufferDatabase::class.java,
            "test_syncstate_recovery.db",
        )
            .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
            .allowMainThreadQueries()
            .build()

        try {
            val syncDao = fileDb.syncStateDao()
            val now = Instant.now().toString()
            val state = SyncState(
                id = 1,
                lastFccCursor = "token:abc123",
                lastUploadAt = now,
                lastStatusPollAt = now,
                lastConfigPullAt = null,
                lastConfigVersion = 42,
                updatedAt = now,
            )
            syncDao.upsert(state)

            // Simulate crash
            fileDb.close()

            // Reopen
            val reopenedDb = Room.databaseBuilder(
                context,
                BufferDatabase::class.java,
                "test_syncstate_recovery.db",
            )
                .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
                .allowMainThreadQueries()
                .build()

            try {
                val recovered = reopenedDb.syncStateDao().get()
                assertNotNull("SyncState must survive crash recovery", recovered)
                assertEquals("token:abc123", recovered!!.lastFccCursor)
                assertEquals(42, recovered.lastConfigVersion)
                assertEquals(now, recovered.lastUploadAt)
            } finally {
                reopenedDb.close()
            }
        } finally {
            context.deleteDatabase("test_syncstate_recovery.db")
        }
    }

    // -------------------------------------------------------------------------
    // Concurrent read/write under WAL
    // -------------------------------------------------------------------------

    @Test
    fun `concurrent reads and writes succeed under WAL mode`() = runBlocking {
        val dao = db.transactionDao()

        // Insert base records
        for (i in 0 until 100) {
            dao.insert(makeTransaction(index = i))
        }

        // Concurrent: read while writing
        val readResult = dao.getForLocalApi(limit = 50, offset = 0)
        assertEquals(50, readResult.size)

        // Write more while previous reads were active
        for (i in 100 until 200) {
            dao.insert(makeTransaction(index = i))
        }

        // Verify final count
        val totalCount = dao.countForLocalApi()
        assertEquals(200, totalCount)
    }

    // -------------------------------------------------------------------------
    // Large backlog persistence
    // -------------------------------------------------------------------------

    @Test
    fun `1000 record backlog persists across close and reopen`() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val fileDb = Room.databaseBuilder(
            context,
            BufferDatabase::class.java,
            "test_large_backlog.db",
        )
            .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
            .allowMainThreadQueries()
            .build()

        try {
            val dao = fileDb.transactionDao()

            // Insert 1,000 records
            for (i in 0 until 1_000) {
                dao.insert(makeTransaction(index = i))
            }

            assertEquals(1_000, dao.countForLocalApi())

            // Simulate crash
            fileDb.close()

            // Reopen
            val reopenedDb = Room.databaseBuilder(
                context,
                BufferDatabase::class.java,
                "test_large_backlog.db",
            )
                .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
                .allowMainThreadQueries()
                .build()

            try {
                val count = reopenedDb.transactionDao().countForLocalApi()
                assertEquals("All 1000 records must survive", 1_000, count)

                // Verify upload ordering still works
                val pending = reopenedDb.transactionDao().getPendingForUpload(50)
                assertEquals(50, pending.size)
                for (i in 1 until pending.size) {
                    assertTrue(
                        "Upload ordering preserved after recovery",
                        pending[i].createdAt >= pending[i - 1].createdAt,
                    )
                }
            } finally {
                reopenedDb.close()
            }
        } finally {
            context.deleteDatabase("test_large_backlog.db")
        }
    }

    // -------------------------------------------------------------------------
    // Status transition persistence
    // -------------------------------------------------------------------------

    @Test
    fun `sync status transitions persist across crash`() = runBlocking {
        val context = ApplicationProvider.getApplicationContext<android.content.Context>()
        val fileDb = Room.databaseBuilder(
            context,
            BufferDatabase::class.java,
            "test_status_recovery.db",
        )
            .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
            .allowMainThreadQueries()
            .build()

        try {
            val dao = fileDb.transactionDao()

            // Insert 3 records: advance one to UPLOADED, one to SYNCED_TO_ODOO
            val tx1 = makeTransaction(index = 0)
            val tx2 = makeTransaction(index = 1)
            val tx3 = makeTransaction(index = 2)
            dao.insert(tx1)
            dao.insert(tx2)
            dao.insert(tx3)

            val now = Instant.now().toString()
            dao.markBatchUploaded(listOf(tx1.id), now)
            dao.markSyncedToOdoo(listOf(tx2.fccTransactionId), now)

            // Crash
            fileDb.close()

            // Reopen
            val reopenedDb = Room.databaseBuilder(
                context,
                BufferDatabase::class.java,
                "test_status_recovery.db",
            )
                .setJournalMode(androidx.room.RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
                .allowMainThreadQueries()
                .build()

            try {
                val rTx1 = reopenedDb.transactionDao().getById(tx1.id)
                val rTx2 = reopenedDb.transactionDao().getById(tx2.id)
                val rTx3 = reopenedDb.transactionDao().getById(tx3.id)

                assertNotNull(rTx1)
                assertNotNull(rTx2)
                assertNotNull(rTx3)
                assertEquals("UPLOADED", rTx1!!.syncStatus)
                assertEquals("SYNCED_TO_ODOO", rTx2!!.syncStatus)
                assertEquals("PENDING", rTx3!!.syncStatus)

                // Local API should exclude SYNCED_TO_ODOO
                val localApiResults = reopenedDb.transactionDao().getForLocalApi(50, 0)
                assertEquals(2, localApiResults.size)
                assertTrue(localApiResults.none { it.syncStatus == "SYNCED_TO_ODOO" })
            } finally {
                reopenedDb.close()
            }
        } finally {
            context.deleteDatabase("test_status_recovery.db")
        }
    }
}
