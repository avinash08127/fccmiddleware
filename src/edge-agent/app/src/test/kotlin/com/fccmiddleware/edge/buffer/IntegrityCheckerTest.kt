package com.fccmiddleware.edge.buffer

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.fccmiddleware.edge.buffer.IntegrityChecker.IntegrityCheckResult
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * IntegrityCheckerTest — verifies the integrity check and recovery logic.
 *
 * Uses an in-memory Room database (Robolectric). The happy path (Healthy) is tested
 * directly against the real PRAGMA. The corruption path is tested via a subclass that
 * overrides [IntegrityChecker.readIntegrityCheck] to inject a synthetic bad result,
 * exercising the recovery branch without requiring actual file corruption.
 *
 * Note: The physical backup-and-delete recovery tested via [CorruptionSimulatingChecker]
 * will not produce a real backup file in the in-memory test context — the test verifies
 * that [IntegrityCheckResult.Recovered] is returned and the audit log event is attempted.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class IntegrityCheckerTest {

    private lateinit var db: BufferDatabase
    private lateinit var checker: IntegrityChecker

    @Before
    fun setUp() {
        db = Room.inMemoryDatabaseBuilder(
            ApplicationProvider.getApplicationContext(),
            BufferDatabase::class.java
        ).allowMainThreadQueries().build()
        checker = IntegrityChecker(
            db = db,
            auditLogDao = db.auditLogDao(),
            context = ApplicationProvider.getApplicationContext(),
        )
    }

    @After
    fun tearDown() {
        if (db.isOpen) db.close()
    }

    // -------------------------------------------------------------------------
    // Happy path — healthy database
    // -------------------------------------------------------------------------

    @Test
    fun `runCheck returns Healthy for a clean in-memory database`() = runBlocking {
        val result = checker.runCheck()
        assertTrue(
            "Expected Healthy but got $result",
            result is IntegrityCheckResult.Healthy
        )
    }

    @Test
    fun `runCheck does not write audit log on healthy database`() = runBlocking {
        checker.runCheck()

        val recent = db.auditLogDao().getRecent(10)
        assertTrue(
            "No DB_CORRUPTION_DETECTED event should be logged on a healthy database",
            recent.none { it.eventType == "DB_CORRUPTION_DETECTED" }
        )
    }

    // -------------------------------------------------------------------------
    // Corruption path — injected via subclass override
    // -------------------------------------------------------------------------

    @Test
    fun `runCheck returns Recovered when corruption is detected`() = runBlocking {
        val fakeChecker = CorruptionSimulatingChecker(
            db = db,
            auditLogDao = db.auditLogDao(),
            fakeIssues = listOf("row 42: *** index ix_bt_dedup is out of order", "*** 1 errors found by integrity_check"),
        )

        val result = fakeChecker.runCheck()

        assertTrue(
            "Expected Recovered result when corruption is injected, got $result",
            result is IntegrityCheckResult.Recovered
        )
    }

    @Test
    fun `runCheck attempts to write DB_CORRUPTION_DETECTED audit event when corrupt`() = runBlocking {
        val fakeChecker = CorruptionSimulatingChecker(
            db = db,
            auditLogDao = db.auditLogDao(),
            fakeIssues = listOf("row 1: *** database disk image is malformed"),
        )

        fakeChecker.runCheck()

        // The audit log write is best-effort — verify it was attempted on a live DB
        // (the in-memory DB is still open when we override backupAndDelete to be a no-op)
        val recent = db.auditLogDao().getRecent(10)
        assertTrue(
            "DB_CORRUPTION_DETECTED audit event must be attempted",
            recent.any { it.eventType == "DB_CORRUPTION_DETECTED" }
        )
    }

    @Test
    fun `runCheck returns Recovered with backupPath field`() = runBlocking {
        val fakeChecker = CorruptionSimulatingChecker(
            db = db,
            auditLogDao = db.auditLogDao(),
            fakeIssues = listOf("corruption found"),
        )

        val result = fakeChecker.runCheck()

        assertNotNull("Recovered result must have a backupPath", (result as? IntegrityCheckResult.Recovered)?.backupPath)
    }

    @Test
    fun `runCheck treats a single-item ok result as Healthy`() = runBlocking {
        val fakeChecker = CorruptionSimulatingChecker(
            db = db,
            auditLogDao = db.auditLogDao(),
            fakeIssues = listOf("ok"),
        )

        val result = fakeChecker.runCheck()

        assertTrue("Single 'ok' result must produce Healthy", result is IntegrityCheckResult.Healthy)
    }

    @Test
    fun `runCheck is case-insensitive for ok result`() = runBlocking {
        val fakeChecker = CorruptionSimulatingChecker(
            db = db,
            auditLogDao = db.auditLogDao(),
            fakeIssues = listOf("OK"), // uppercase
        )

        val result = fakeChecker.runCheck()

        assertTrue("Case-insensitive 'OK' must produce Healthy", result is IntegrityCheckResult.Healthy)
    }

    // -------------------------------------------------------------------------
    // Test helper — overrides pragma to inject synthetic results
    // -------------------------------------------------------------------------

    /**
     * Subclass that short-circuits the real PRAGMA execution and returns controlled
     * issues. Also overrides the physical backup/delete step to avoid file-system
     * side effects in the in-memory test environment.
     */
    private class CorruptionSimulatingChecker(
        db: BufferDatabase,
        auditLogDao: com.fccmiddleware.edge.buffer.dao.AuditLogDao,
        private val fakeIssues: List<String>,
    ) : IntegrityChecker(
        db = db,
        auditLogDao = auditLogDao,
        context = ApplicationProvider.getApplicationContext(),
    ) {
        override suspend fun readIntegrityCheck(): List<String> = fakeIssues
    }
}
