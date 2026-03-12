package com.fccmiddleware.edge.buffer.entity

import androidx.room.ColumnInfo
import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * Locally cached product mapping from the cloud config.
 *
 * Maps FCC-native product codes to canonical (Odoo) product codes.
 * Replaced in full on each config refresh.
 */
@Entity(tableName = "local_products")
data class LocalProduct(
    @PrimaryKey
    @ColumnInfo(name = "fcc_product_code")
    val fccProductCode: String,

    @ColumnInfo(name = "canonical_product_code")
    val canonicalProductCode: String,

    @ColumnInfo(name = "display_name")
    val displayName: String,

    /** Boolean: 1=active, 0=inactive */
    @ColumnInfo(name = "active")
    val active: Int = 1,
)
