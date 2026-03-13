package com.fccmiddleware.edge.smoke

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.fccmiddleware.edge.buffer.BufferDatabaseFactory
import com.fccmiddleware.edge.security.KeystoreManager
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

/**
 * TG-002 — Instrumented (androidTest) smoke tests.
 *
 * This directory establishes the androidTest scaffold for on-device integration
 * tests identified in TG-002. The recommended additions are:
 *
 *   - Room migration verification (versions 1 → 2 → 3 → 4 → 5)
 *   - EncryptedSharedPreferences round-trip (encrypt/decrypt a known value)
 *   - KeystoreManager encrypt/decrypt cycle using real Android Keystore
 *   - EdgeAgentForegroundService start/stop with a live Android context
 *
 * These tests require a physical device or emulator running API 31+ and cannot
 * run under Robolectric because they exercise the Android Keystore hardware and
 * the Room migration path with real SQLite.
 */
@RunWith(AndroidJUnit4::class)
class AndroidSmokeTest {

    /**
     * Sanity check: verifies the test instrumentation context is available and
     * the correct application package is under test.
     *
     * This test acts as a compile-time guard — if the androidTest directory is
     * present and the Gradle configuration is correct, this test will pass on
     * any device or emulator.
     */
    @Test
    fun applicationPackageIsCorrect() {
        val appContext = InstrumentationRegistry.getInstrumentation().targetContext
        assertEquals("com.fccmiddleware.edge", appContext.packageName)
    }

    @Test
    fun keystoreHardwareBackingStatusCanBeInspected() {
        val keystoreManager = KeystoreManager()
        val alias = "android-smoke-keystore-status"

        assertNotNull(keystoreManager.storeSecret(alias, "smoke-secret"))
        assertNotNull(
            "Expected KeyInfo hardware-backing inspection to return a boolean",
            keystoreManager.isHardwareBacked(alias),
        )

        keystoreManager.deleteKey(alias)
    }

    @Test
    fun bufferDatabaseFileIsNotPlaintextSQLite() {
        val appContext = InstrumentationRegistry.getInstrumentation().targetContext
        val dbName = "android-smoke-buffer-encrypted.db"
        val passphraseFile = File(
            File(appContext.noBackupFilesDir, "database-security"),
            "$dbName.passphrase.enc",
        )

        appContext.deleteDatabase(dbName)
        File(appContext.getDatabasePath(dbName).absolutePath + "-wal").delete()
        File(appContext.getDatabasePath(dbName).absolutePath + "-shm").delete()
        passphraseFile.delete()

        val db = BufferDatabaseFactory.create(appContext, KeystoreManager(), dbName)
        try {
            db.clearAllData()
        } finally {
            db.close()
        }

        val header = ByteArray("SQLite format 3\u0000".length)
        appContext.getDatabasePath(dbName).inputStream().use { it.read(header) }

        assertFalse(
            "Encrypted SQLCipher database should not expose the plaintext SQLite header",
            header.contentEquals("SQLite format 3\u0000".toByteArray(Charsets.US_ASCII)),
        )

        appContext.deleteDatabase(dbName)
        File(appContext.getDatabasePath(dbName).absolutePath + "-wal").delete()
        File(appContext.getDatabasePath(dbName).absolutePath + "-shm").delete()
        passphraseFile.delete()
    }
}
