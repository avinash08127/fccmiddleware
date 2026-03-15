package com.fccmiddleware.edge.buffer

import android.content.Context
import androidx.room.Room
import androidx.room.RoomDatabase
import com.fccmiddleware.edge.logging.AppLogger
import com.fccmiddleware.edge.security.KeystoreManager
import net.zetetic.database.sqlcipher.SQLiteDatabase
import net.zetetic.database.sqlcipher.SupportOpenHelperFactory
import java.io.File
import java.security.SecureRandom
import java.util.Base64

/**
 * Prepares SQLCipher support for the Room buffer database and migrates an
 * existing plaintext SQLite file in place on first encrypted startup.
 *
 * The SQLCipher passphrase is a random value stored as:
 * 1. plaintext in memory only
 * 2. Keystore-encrypted bytes on disk in no-backup storage
 */
internal class BufferDatabaseEncryption(
    context: Context,
    private val keystoreManager: KeystoreManager,
    private val databaseName: String,
) {
    companion object {
        private const val TAG = "BufferDatabaseEncryption"
        private const val PASSPHRASE_DIRECTORY = "database-security"
        private const val PASSPHRASE_FILE_SUFFIX = ".passphrase.enc"
        private const val TEMP_ENCRYPTED_SUFFIX = ".sqlcipher-tmp"
        private const val PASSPHRASE_BYTES = 32
        private val SQLITE_HEADER = "SQLite format 3\u0000".toByteArray(Charsets.US_ASCII)
    }

    private val appContext = context.applicationContext
    private val databaseFile = appContext.getDatabasePath(databaseName)
    private val encryptedPassphraseFile = File(
        File(appContext.noBackupFilesDir, PASSPHRASE_DIRECTORY).also { it.mkdirs() },
        "$databaseName$PASSPHRASE_FILE_SUFFIX",
    )

    fun createFactory(): SupportOpenHelperFactory {
        System.loadLibrary("sqlcipher")

        val databaseState = detectDatabaseState()
        val passphrase = loadPersistedPassphrase()?.takeIf { it.isNotBlank() }
            ?: recoverOrCreatePassphrase(databaseState)

        if (databaseState == DatabaseState.PLAINTEXT) {
            migratePlaintextDatabase(passphrase)
        }

        return SupportOpenHelperFactory(passphrase.toByteArray())
    }

    private fun recoverOrCreatePassphrase(databaseState: DatabaseState): String {
        if (encryptedPassphraseFile.exists()) {
            AppLogger.w(TAG, "Database passphrase unavailable — regenerating encrypted buffer key material")
            encryptedPassphraseFile.delete()
            keystoreManager.deleteKey(KeystoreManager.ALIAS_BUFFER_DB_PASSPHRASE)
        }

        if (databaseState == DatabaseState.ENCRYPTED) {
            AppLogger.w(TAG, "Encrypted database cannot be unlocked — recreating local buffer database")
            deleteDatabaseArtifacts(databaseFile)
        }

        return generateAndPersistPassphrase()
    }

    private fun loadPersistedPassphrase(): String? {
        if (!encryptedPassphraseFile.exists()) return null

        return try {
            keystoreManager.retrieveSecret(
                KeystoreManager.ALIAS_BUFFER_DB_PASSPHRASE,
                encryptedPassphraseFile.readBytes(),
            )
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to load encrypted database passphrase", e)
            null
        }
    }

    private fun generateAndPersistPassphrase(): String {
        val raw = ByteArray(PASSPHRASE_BYTES).also(SecureRandom()::nextBytes)
        val passphrase = Base64.getEncoder().withoutPadding().encodeToString(raw)
        val encrypted = keystoreManager.storeSecret(
            KeystoreManager.ALIAS_BUFFER_DB_PASSPHRASE,
            passphrase,
        ) ?: throw IllegalStateException("Failed to protect SQLCipher passphrase with Android Keystore")

        encryptedPassphraseFile.parentFile?.mkdirs()
        encryptedPassphraseFile.writeBytes(encrypted)
        return passphrase
    }

    private fun migratePlaintextDatabase(passphrase: String) {
        if (!databaseFile.exists()) return

        val tempEncryptedFile = File(databaseFile.parentFile, databaseFile.name + TEMP_ENCRYPTED_SUFFIX)
        deleteDatabaseArtifacts(tempEncryptedFile)

        AppLogger.i(TAG, "Migrating plaintext buffer database to SQLCipher")

        var plainDb: SQLiteDatabase? = null
        try {
            plainDb = SQLiteDatabase.openOrCreateDatabase(
                databaseFile,
                "",
                null,
                null,
            )

            val userVersion = plainDb.getUserVersion()
            val escapedPath = tempEncryptedFile.absolutePath.replace("'", "''")
            val escapedPassphrase = passphrase.replace("'", "''")

            plainDb.execSQL("ATTACH DATABASE '$escapedPath' AS encrypted KEY '$escapedPassphrase'")
            plainDb.rawQuery("SELECT sqlcipher_export('encrypted')", emptyArray()).use { cursor ->
                while (cursor.moveToNext()) {
                    // sqlcipher_export returns a single row; iterating forces execution.
                }
            }
            plainDb.execSQL("PRAGMA encrypted.user_version = $userVersion")
            plainDb.execSQL("DETACH DATABASE encrypted")
            plainDb.close()
            plainDb = null

            deleteDatabaseArtifacts(databaseFile)
            if (!tempEncryptedFile.renameTo(databaseFile)) {
                throw IllegalStateException("Failed to replace plaintext database with encrypted copy")
            }

            AppLogger.i(TAG, "SQLCipher migration completed for buffer database")
        } catch (e: Exception) {
            deleteDatabaseArtifacts(tempEncryptedFile)
            throw IllegalStateException("Failed to migrate plaintext buffer database to SQLCipher", e)
        } finally {
            try {
                plainDb?.close()
            } catch (_: Exception) {
                // Best effort cleanup
            }
        }
    }

    private fun SQLiteDatabase.getUserVersion(): Int =
        rawQuery("PRAGMA user_version", emptyArray()).use { cursor ->
            if (cursor.moveToFirst()) cursor.getInt(0) else 0
        }

    private fun detectDatabaseState(): DatabaseState {
        if (!databaseFile.exists()) return DatabaseState.MISSING

        return try {
            val header = ByteArray(SQLITE_HEADER.size)
            val bytesRead = databaseFile.inputStream().use { it.read(header) }
            if (bytesRead == SQLITE_HEADER.size && header.contentEquals(SQLITE_HEADER)) {
                DatabaseState.PLAINTEXT
            } else {
                DatabaseState.ENCRYPTED
            }
        } catch (e: Exception) {
            AppLogger.e(TAG, "Failed to inspect buffer database header", e)
            DatabaseState.ENCRYPTED
        }
    }

    private fun deleteDatabaseArtifacts(baseFile: File) {
        listOf(
            baseFile,
            File(baseFile.absolutePath + "-wal"),
            File(baseFile.absolutePath + "-shm"),
            File(baseFile.absolutePath + "-journal"),
        ).forEach { file ->
            if (file.exists() && !file.delete()) {
                file.deleteOnExit()
            }
        }
    }

    private enum class DatabaseState {
        MISSING,
        PLAINTEXT,
        ENCRYPTED,
    }
}

object BufferDatabaseFactory {
    fun create(
        context: Context,
        keystoreManager: KeystoreManager,
        databaseName: String = BufferDatabase.DATABASE_NAME,
    ): BufferDatabase {
        val appContext = context.applicationContext
        val encryption = BufferDatabaseEncryption(appContext, keystoreManager, databaseName)

        return Room.databaseBuilder(
            appContext,
            BufferDatabase::class.java,
            databaseName,
        )
            .openHelperFactory(encryption.createFactory())
            .setJournalMode(RoomDatabase.JournalMode.WRITE_AHEAD_LOGGING)
            .addMigrations(
                BufferDatabase.MIGRATION_1_2,
                BufferDatabase.MIGRATION_2_3,
                BufferDatabase.MIGRATION_3_4,
                BufferDatabase.MIGRATION_4_5,
                BufferDatabase.MIGRATION_5_6,
                BufferDatabase.MIGRATION_6_7,
                BufferDatabase.MIGRATION_7_8,
                BufferDatabase.MIGRATION_8_9,
                BufferDatabase.MIGRATION_9_10,
                BufferDatabase.MIGRATION_10_11,
                BufferDatabase.MIGRATION_11_12,
            )
            // Transactions are re-uploaded from the buffer, so destructive recovery
            // is preferable to a crash-loop on a corrupted or unexpected schema.
            .fallbackToDestructiveMigration()
            .build()
    }
}
