package com.fccmiddleware.agent.buffer

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase

@Database(
    entities = [
        BufferedTransactionEntity::class,
        PreAuthRecordEntity::class,
    ],
    version = 1,
    exportSchema = true
)
abstract class AppDatabase : RoomDatabase() {
    abstract fun transactionDao(): TransactionBufferDao
    abstract fun preAuthDao(): PreAuthDao

    companion object {
        fun create(context: Context): AppDatabase {
            return Room.databaseBuilder(
                context.applicationContext,
                AppDatabase::class.java,
                "fcc_agent.db"
            )
                .setJournalMode(JournalMode.WRITE_AHEAD_LOGGING)
                .build()
        }
    }
}
