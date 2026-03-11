package com.fccmiddleware.edge.buffer

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.dao.NozzleDao
import com.fccmiddleware.edge.buffer.dao.PreAuthDao
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import com.fccmiddleware.edge.buffer.entity.AuditLog
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.buffer.entity.Nozzle
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import com.fccmiddleware.edge.buffer.entity.SyncState

/**
 * Room database for the FCC Edge Agent.
 *
 * Six entities:
 *   - BufferedTransaction — FCC dispense transactions (offline buffer)
 *   - PreAuthRecord       — Pre-authorization lifecycle records
 *   - Nozzle              — Odoo ↔ FCC pump/nozzle mapping (from cloud config)
 *   - SyncState           — Single-row cloud sync state cursor
 *   - AgentConfig         — Single-row cached SiteConfig JSON
 *   - AuditLog            — Local diagnostic audit trail
 *
 * WAL mode is set via setJournalMode(JournalMode.WRITE_AHEAD_LOGGING) in the builder,
 * which persists across connections for the database file.
 *
 * Schema is exported to app/schemas/ for migration testing and CI validation.
 */
@Database(
    entities = [
        BufferedTransaction::class,
        PreAuthRecord::class,
        Nozzle::class,
        SyncState::class,
        AgentConfig::class,
        AuditLog::class,
    ],
    version = 1,
    exportSchema = true
)
abstract class BufferDatabase : RoomDatabase() {

    abstract fun transactionDao(): TransactionBufferDao
    abstract fun preAuthDao(): PreAuthDao
    abstract fun nozzleDao(): NozzleDao
    abstract fun syncStateDao(): SyncStateDao
    abstract fun agentConfigDao(): AgentConfigDao
    abstract fun auditLogDao(): AuditLogDao

    companion object {
        fun create(context: Context): BufferDatabase {
            return Room.databaseBuilder(
                context.applicationContext,
                BufferDatabase::class.java,
                "fcc_buffer.db"
            )
                .setJournalMode(JournalMode.WRITE_AHEAD_LOGGING)
                .build()
        }
    }
}
