package com.fccmiddleware.edge.buffer

import android.content.Context
import androidx.room.migration.Migration
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.TypeConverters
import androidx.sqlite.db.SupportSQLiteDatabase
import com.fccmiddleware.edge.buffer.dao.AgentConfigDao
import com.fccmiddleware.edge.buffer.dao.AuditLogDao
import com.fccmiddleware.edge.buffer.dao.NozzleDao
import com.fccmiddleware.edge.buffer.dao.PreAuthDao
import com.fccmiddleware.edge.buffer.dao.SiteDataDao
import com.fccmiddleware.edge.buffer.dao.SyncStateDao
import com.fccmiddleware.edge.buffer.dao.TransactionBufferDao
import com.fccmiddleware.edge.buffer.entity.AgentConfig
import com.fccmiddleware.edge.buffer.entity.AuditLog
import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.buffer.entity.LocalNozzle
import com.fccmiddleware.edge.buffer.entity.LocalProduct
import com.fccmiddleware.edge.buffer.entity.LocalPump
import com.fccmiddleware.edge.buffer.entity.Nozzle
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import com.fccmiddleware.edge.buffer.entity.SiteInfo
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
 *   - SiteInfo            — Site identity & FCC metadata (from cloud config)
 *   - LocalProduct        — FCC ↔ canonical product code mapping
 *   - LocalPump           — Odoo ↔ FCC pump number mapping
 *   - LocalNozzle         — Odoo ↔ FCC nozzle mapping (site data layer)
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
        SiteInfo::class,
        LocalProduct::class,
        LocalPump::class,
        LocalNozzle::class,
    ],
    version = 8,
    exportSchema = true
)
@TypeConverters(PreAuthStatusConverters::class)
abstract class BufferDatabase : RoomDatabase() {

    abstract fun transactionDao(): TransactionBufferDao
    abstract fun preAuthDao(): PreAuthDao
    abstract fun nozzleDao(): NozzleDao
    abstract fun syncStateDao(): SyncStateDao
    abstract fun agentConfigDao(): AgentConfigDao
    abstract fun auditLogDao(): AuditLogDao
    abstract fun siteDataDao(): SiteDataDao

    /**
     * AF-013: Truncate all tables so re-provisioning starts with a clean database.
     * Prevents cross-site data contamination when a device is re-provisioned for a different site.
     */
    fun clearAllData() {
        clearAllTables()
    }

    companion object {
        val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    """
                    ALTER TABLE pre_auth_records
                    ADD COLUMN unit_price_minor_per_litre INTEGER
                    """.trimIndent(),
                )
            }
        }

        /**
         * Migration 2 → 3: Create site master data tables.
         *
         * These tables are always repopulated from cloud config, so a destructive
         * fallback is acceptable — but we provide a proper migration so that
         * existing tables (transactions, pre-auth, sync state) are preserved.
         */
        val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS site_info (
                        site_code TEXT NOT NULL PRIMARY KEY,
                        site_name TEXT NOT NULL,
                        legal_entity_code TEXT NOT NULL,
                        timezone TEXT NOT NULL,
                        currency_code TEXT NOT NULL,
                        operating_model TEXT NOT NULL,
                        fcc_vendor TEXT,
                        fcc_model TEXT,
                        ingestion_mode TEXT,
                        synced_at TEXT NOT NULL
                    )
                    """.trimIndent(),
                )
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS local_products (
                        fcc_product_code TEXT NOT NULL PRIMARY KEY,
                        canonical_product_code TEXT NOT NULL,
                        display_name TEXT NOT NULL,
                        active INTEGER NOT NULL DEFAULT 1
                    )
                    """.trimIndent(),
                )
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS local_pumps (
                        odoo_pump_number INTEGER NOT NULL PRIMARY KEY,
                        fcc_pump_number INTEGER NOT NULL,
                        display_name TEXT NOT NULL DEFAULT ''
                    )
                    """.trimIndent(),
                )
                db.execSQL(
                    """
                    CREATE TABLE IF NOT EXISTS local_nozzles (
                        odoo_nozzle_number INTEGER NOT NULL,
                        odoo_pump_number INTEGER NOT NULL,
                        fcc_nozzle_number INTEGER NOT NULL,
                        fcc_pump_number INTEGER NOT NULL,
                        product_code TEXT NOT NULL,
                        PRIMARY KEY (odoo_nozzle_number, odoo_pump_number)
                    )
                    """.trimIndent(),
                )
            }
        }

        /**
         * Migration 3 → 4: Add WebSocket backward-compat columns for Odoo POS cart workflow.
         *
         * These columns are only used by the OdooWebSocketServer and do not affect
         * the cloud sync pipeline. All columns are nullable/default so existing rows
         * are unaffected.
         */
        val MIGRATION_3_4 = object : Migration(3, 4) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN order_uuid TEXT")
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN odoo_order_id TEXT")
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN add_to_cart INTEGER NOT NULL DEFAULT 0")
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN payment_id TEXT")
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN is_discard INTEGER NOT NULL DEFAULT 0")
            }
        }

        val MIGRATION_4_5 = object : Migration(4, 5) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN fiscal_attempts INTEGER NOT NULL DEFAULT 0")
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN last_fiscal_attempt_at TEXT")
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN fiscal_status TEXT NOT NULL DEFAULT 'NONE'")
            }
        }

        /**
         * Migration 5 → 6: AF-022 — Add acknowledged_at column for POS transaction acknowledge.
         *
         * The acknowledge endpoint was a no-op (only counted records). This column
         * tracks when each transaction was marked as consumed by POS, and local API
         * queries now exclude acknowledged records to prevent double-consumption.
         */
        val MIGRATION_5_6 = object : Migration(5, 6) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN acknowledged_at TEXT")
            }
        }

        /**
         * Migration 6 → 7: AF-026 — Add fiscalization context columns.
         *
         * Stores payment_type, customer_tax_id, customer_name, and customer_id_type
         * so that fiscalization retries use the correct values instead of hardcoding CASH.
         * Existing records default to CASH for payment_type and NULL for customer fields.
         */
        val MIGRATION_6_7 = object : Migration(6, 7) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN payment_type TEXT NOT NULL DEFAULT 'CASH'")
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN fiscal_customer_tax_id TEXT")
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN fiscal_customer_name TEXT")
                db.execSQL("ALTER TABLE buffered_transactions ADD COLUMN fiscal_customer_id_type INTEGER")
            }
        }

        /**
         * Migration 7 → 8: AF-035 — Add last_upload_attempt_at to sync_state.
         *
         * Tracks the timestamp of the most recent upload attempt (successful or failed),
         * so telemetry can distinguish lastSyncAttemptUtc from lastSuccessfulSyncUtc.
         */
        val MIGRATION_7_8 = object : Migration(7, 8) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE sync_state ADD COLUMN last_upload_attempt_at TEXT")
            }
        }

        fun create(context: Context): BufferDatabase {
            return Room.databaseBuilder(
                context.applicationContext,
                BufferDatabase::class.java,
                "fcc_buffer.db"
            )
                .setJournalMode(JournalMode.WRITE_AHEAD_LOGGING)
                .addMigrations(MIGRATION_1_2, MIGRATION_2_3, MIGRATION_3_4, MIGRATION_4_5, MIGRATION_5_6, MIGRATION_6_7, MIGRATION_7_8)
                // Fallback for any database version gap not covered by explicit migrations.
                // Transactions are re-uploaded from the buffer, so losing local rows is
                // recoverable. Prevents a crash-loop on devices with an unexpected schema.
                .fallbackToDestructiveMigration()
                .build()
        }
    }
}
