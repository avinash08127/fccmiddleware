package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * Single-row table holding site identity and FCC configuration metadata.
 *
 * Populated from cloud config after registration. Replaced in full on each
 * config refresh — safe to destructively recreate since the cloud is the
 * source of truth.
 */
@Entity(tableName = "site_info")
data class SiteInfo(
    /** Site code — acts as PK (one site per agent) */
    @PrimaryKey
    @ColumnInfo(name = "site_code")
    val siteCode: String,

    @ColumnInfo(name = "site_name")
    val siteName: String,

    @ColumnInfo(name = "legal_entity_code")
    val legalEntityCode: String,

    @ColumnInfo(name = "timezone")
    val timezone: String,

    @ColumnInfo(name = "currency_code")
    val currencyCode: String,

    @ColumnInfo(name = "operating_model")
    val operatingModel: String,

    @ColumnInfo(name = "fcc_vendor")
    val fccVendor: String? = null,

    @ColumnInfo(name = "fcc_model")
    val fccModel: String? = null,

    @ColumnInfo(name = "ingestion_mode")
    val ingestionMode: String? = null,

    /** ISO 8601 UTC; when this row was last synced from cloud */
    @ColumnInfo(name = "synced_at")
    val syncedAt: String,
)
