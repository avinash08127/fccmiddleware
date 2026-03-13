package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * Locally-buffered FCC dispense transaction.
 *
 * Edge sync states: PENDING → UPLOADED → SYNCED_TO_ODOO → ARCHIVED → (deleted)
 *                   PENDING → DEAD_LETTER → (deleted after retention)
 *
 * Upload order is created_at ASC (oldest first). A failed record is never skipped.
 * pumpNumber / nozzleNumber are FCC numbers as received from the FCC.
 * All timestamps: ISO 8601 UTC TEXT. Money: Long minor units. UUIDs: TEXT.
 */
@Entity(
    tableName = "buffered_transactions",
    indices = [
        // Deduplication: prevents duplicate FCC transactions from being buffered twice
        Index(value = ["fcc_transaction_id", "site_code"], unique = true, name = "ix_bt_dedup"),
        // Upload worker: find PENDING records ordered by time (chronological replay)
        Index(value = ["sync_status", "created_at"], name = "ix_bt_sync_status"),
        // Local API: transaction queries by pump, excluding SYNCED_TO_ODOO
        Index(
            value = ["sync_status", "pump_number", "completed_at"],
            orders = [Index.Order.ASC, Index.Order.ASC, Index.Order.DESC],
            name = "ix_bt_local_api"
        ),
        // Retention cleanup worker
        Index(value = ["sync_status", "updated_at"], name = "ix_bt_cleanup"),
    ]
)
data class BufferedTransaction(
    @PrimaryKey
    @ColumnInfo(name = "id")
    val id: String,

    @ColumnInfo(name = "fcc_transaction_id")
    val fccTransactionId: String,

    @ColumnInfo(name = "site_code")
    val siteCode: String,

    /** FCC pump number as received from the FCC (NOT Odoo pump number) */
    @ColumnInfo(name = "pump_number")
    val pumpNumber: Int,

    /** FCC nozzle number as received from the FCC */
    @ColumnInfo(name = "nozzle_number")
    val nozzleNumber: Int,

    @ColumnInfo(name = "product_code")
    val productCode: String,

    /** Microlitres (SQLite INTEGER = 64-bit) */
    @ColumnInfo(name = "volume_microlitres")
    val volumeMicrolitres: Long,

    /** Minor units (cents). NEVER floating point. */
    @ColumnInfo(name = "amount_minor_units")
    val amountMinorUnits: Long,

    /** Minor units per litre. NEVER floating point. */
    @ColumnInfo(name = "unit_price_minor_per_litre")
    val unitPriceMinorPerLitre: Long,

    @ColumnInfo(name = "currency_code")
    val currencyCode: String,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "started_at")
    val startedAt: String,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "completed_at")
    val completedAt: String,

    @ColumnInfo(name = "fiscal_receipt_number")
    val fiscalReceiptNumber: String?,

    @ColumnInfo(name = "fcc_vendor")
    val fccVendor: String,

    @ColumnInfo(name = "attendant_id")
    val attendantId: String?,

    /** TransactionStatus enum: PENDING | CONFIRMED | FAILED */
    @ColumnInfo(name = "status")
    val status: String = "PENDING",

    /** SyncStatus: PENDING | UPLOADED | SYNCED_TO_ODOO | ARCHIVED | DEAD_LETTER */
    @ColumnInfo(name = "sync_status")
    val syncStatus: String = "PENDING",

    /** RELAY | CLOUD_DIRECT | BUFFER_ALWAYS */
    @ColumnInfo(name = "ingestion_source")
    val ingestionSource: String,

    /** Raw FCC payload JSON; may be null if adapter did not preserve it */
    @ColumnInfo(name = "raw_payload_json")
    val rawPayloadJson: String?,

    @ColumnInfo(name = "correlation_id")
    val correlationId: String,

    @ColumnInfo(name = "upload_attempts")
    val uploadAttempts: Int = 0,

    /** ISO 8601 UTC; null until first upload attempt */
    @ColumnInfo(name = "last_upload_attempt_at")
    val lastUploadAttemptAt: String?,

    @ColumnInfo(name = "last_upload_error")
    val lastUploadError: String?,

    // ── WebSocket backward-compat fields (Odoo POS cart workflow) ──────────
    @ColumnInfo(name = "order_uuid")
    val orderUuid: String? = null,

    @ColumnInfo(name = "odoo_order_id")
    val odooOrderId: String? = null,

    @ColumnInfo(name = "add_to_cart", defaultValue = "0")
    val addToCart: Boolean = false,

    @ColumnInfo(name = "payment_id")
    val paymentId: String? = null,

    @ColumnInfo(name = "is_discard", defaultValue = "0")
    val isDiscard: Boolean = false,

    // ── AF-026: Fiscalization context fields ────────────────────────────────
    /** Payment method for fiscal receipt (CASH, CCARD, EMONEY, INVOICE, CHEQUE). */
    @ColumnInfo(name = "payment_type", defaultValue = "'CASH'")
    val paymentType: String = "CASH",

    /** Customer TIN for TRA fiscal receipts. Populated from pre-auth or POS. */
    @ColumnInfo(name = "fiscal_customer_tax_id")
    val fiscalCustomerTaxId: String? = null,

    /** Customer display name on the fiscal receipt. */
    @ColumnInfo(name = "fiscal_customer_name")
    val fiscalCustomerName: String? = null,

    /** TRA CustIdType (1=TIN, 2=DL, 3=Voters, 4=Passport, 5=NID, 6=NIL). */
    @ColumnInfo(name = "fiscal_customer_id_type")
    val fiscalCustomerIdType: Int? = null,

    // ── Fiscalization retry tracking (GAP-7) ──────────────────────────────
    @ColumnInfo(name = "fiscal_attempts", defaultValue = "0")
    val fiscalAttempts: Int = 0,

    @ColumnInfo(name = "last_fiscal_attempt_at")
    val lastFiscalAttemptAt: String? = null,

    /** NONE | PENDING | SUCCESS | DEAD_LETTER */
    @ColumnInfo(name = "fiscal_status", defaultValue = "'NONE'")
    val fiscalStatus: String = "NONE",

    /** AF-022: ISO 8601 UTC; null until acknowledged by POS via POST /api/v1/transactions/acknowledge. */
    @ColumnInfo(name = "acknowledged_at")
    val acknowledgedAt: String? = null,

    @ColumnInfo(name = "schema_version")
    val schemaVersion: Int = 1,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "created_at")
    val createdAt: String,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "updated_at")
    val updatedAt: String,
)
