package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey
import com.fccmiddleware.edge.adapter.common.PreAuthStatus

/**
 * Pre-authorization record stored locally on the Edge Agent.
 *
 * Pre-auth is the top latency path: POST /api/preauth must respond
 * based on LAN-only work. Cloud forwarding is always async and never
 * on the request path.
 *
 * All timestamps: ISO 8601 UTC TEXT. Money: Long minor units. UUIDs: TEXT.
 * customer_tax_id is sensitive — NEVER log this field.
 */
@Entity(
    tableName = "pre_auth_records",
    indices = [
        // Idempotency: prevent duplicate pre-auth creation from Odoo retries
        Index(value = ["odoo_order_id", "site_code"], unique = true, name = "ix_par_idemp"),
        // Cloud forward worker: find unsynced records ordered by time
        Index(value = ["is_cloud_synced", "created_at"], name = "ix_par_unsent"),
        // Expiry worker: find active records approaching or past expiry
        Index(value = ["status", "expires_at"], name = "ix_par_expiry"),
    ]
)
data class PreAuthRecord(
    @PrimaryKey
    @ColumnInfo(name = "id")
    val id: String,

    @ColumnInfo(name = "site_code")
    val siteCode: String,

    @ColumnInfo(name = "odoo_order_id")
    val odooOrderId: String,

    /** FCC pump number (after Odoo → FCC translation via Nozzle table) */
    @ColumnInfo(name = "pump_number")
    val pumpNumber: Int,

    /** FCC nozzle number */
    @ColumnInfo(name = "nozzle_number")
    val nozzleNumber: Int,

    @ColumnInfo(name = "product_code")
    val productCode: String,

    @ColumnInfo(name = "currency_code")
    val currencyCode: String,

    /** Minor units (cents). NEVER floating point. */
    @ColumnInfo(name = "requested_amount_minor_units")
    val requestedAmountMinorUnits: Long,

    /** Minor units per litre. Null for legacy rows created before unit price was persisted. */
    @ColumnInfo(name = "unit_price_minor_per_litre")
    val unitPrice: Long? = null,

    /** Minor units. Null until FCC authorizes. */
    @ColumnInfo(name = "authorized_amount_minor_units")
    val authorizedAmountMinorUnits: Long?,

    /** PreAuthStatus: PENDING | AUTHORIZED | DISPENSING | COMPLETED | EXPIRED | CANCELLED | FAILED */
    @ColumnInfo(name = "status")
    val status: PreAuthStatus = PreAuthStatus.PENDING,

    @ColumnInfo(name = "fcc_correlation_id")
    val fccCorrelationId: String?,

    @ColumnInfo(name = "fcc_authorization_code")
    val fccAuthorizationCode: String?,

    @ColumnInfo(name = "failure_reason")
    val failureReason: String?,

    @ColumnInfo(name = "customer_name")
    val customerName: String?,

    /** SENSITIVE — never log */
    @ColumnInfo(name = "customer_tax_id")
    val customerTaxId: String?,

    @ColumnInfo(name = "raw_fcc_response")
    val rawFccResponse: String?,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "requested_at")
    val requestedAt: String,

    /** ISO 8601 UTC; null until FCC authorizes */
    @ColumnInfo(name = "authorized_at")
    val authorizedAt: String?,

    /** ISO 8601 UTC; null until dispense completes */
    @ColumnInfo(name = "completed_at")
    val completedAt: String?,

    /** ISO 8601 UTC; pre-auth TTL from site config */
    @ColumnInfo(name = "expires_at")
    val expiresAt: String,

    /** Boolean: 0/1 */
    @ColumnInfo(name = "is_cloud_synced")
    val isCloudSynced: Int = 0,

    @ColumnInfo(name = "cloud_sync_attempts")
    val cloudSyncAttempts: Int = 0,

    /** ISO 8601 UTC; null until first cloud sync attempt */
    @ColumnInfo(name = "last_cloud_sync_attempt_at")
    val lastCloudSyncAttemptAt: String?,

    @ColumnInfo(name = "schema_version")
    val schemaVersion: Int = 1,

    /** ISO 8601 UTC */
    @ColumnInfo(name = "created_at")
    val createdAt: String,
)
