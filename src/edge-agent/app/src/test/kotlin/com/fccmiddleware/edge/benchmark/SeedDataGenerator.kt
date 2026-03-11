package com.fccmiddleware.edge.benchmark

import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.buffer.entity.PreAuthRecord
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID

/**
 * SeedDataGenerator — produces representative synthetic datasets for benchmarks.
 *
 * Generates a 30,000-record BufferedTransaction dataset matching the distribution
 * described in edge-agent-performance-budgets.md §6.
 */
object SeedDataGenerator {

    private val SITES = listOf("SITE_001", "SITE_002", "SITE_003")
    private val VENDORS = listOf("DOMS", "ADVATEC", "PETRONITE")
    private val PRODUCTS = listOf("ULP95", "ULP93", "DIESEL", "LPG")
    private val CURRENCIES = listOf("MWK", "TZS", "BWP", "ZMW")

    /**
     * Generate [count] buffered transaction records with a representative status distribution.
     *
     * Distribution: 70% PENDING, 20% UPLOADED, 10% SYNCED_TO_ODOO
     * Timestamps: spread over a 30-day window ending at [baseTime]
     */
    fun transactions(
        count: Int = 30_000,
        baseTime: Instant = Instant.now(),
    ): List<BufferedTransaction> {
        val windowSeconds = ChronoUnit.DAYS.getDuration().seconds * 30
        return (0 until count).map { i ->
            val syncStatus = when {
                i % 10 == 9 -> "SYNCED_TO_ODOO"
                i % 10 >= 7 -> "UPLOADED"
                else -> "PENDING"
            }
            val createdAt = baseTime
                .minusSeconds(windowSeconds * i.toLong() / count)
                .toString()
            val completedAt = baseTime
                .minusSeconds(windowSeconds * i.toLong() / count)
                .minusSeconds(30)
                .toString()
            val updatedAt = if (syncStatus != "PENDING") createdAt else createdAt
            BufferedTransaction(
                id = UUID.randomUUID().toString(),
                fccTransactionId = "FCC-${i.toString().padStart(8, '0')}",
                siteCode = SITES[i % SITES.size],
                pumpNumber = (i % 4) + 1,
                nozzleNumber = (i % 2) + 1,
                productCode = PRODUCTS[i % PRODUCTS.size],
                volumeMicrolitres = ((i % 50) + 10) * 1_000_000L,
                amountMinorUnits = ((i % 100) + 10) * 500L,
                unitPriceMinorPerLitre = 1_500L + (i % 50),
                currencyCode = CURRENCIES[i % CURRENCIES.size],
                startedAt = baseTime
                    .minusSeconds(windowSeconds * i.toLong() / count)
                    .minusSeconds(90)
                    .toString(),
                completedAt = completedAt,
                fiscalReceiptNumber = if (i % 5 == 0) null else "FISC-${i.toString().padStart(8, '0')}",
                fccVendor = VENDORS[i % VENDORS.size],
                attendantId = if (i % 4 == 0) null else "ATT-${(i % 20).toString().padStart(4, '0')}",
                status = "PENDING",
                syncStatus = syncStatus,
                ingestionSource = "RELAY",
                rawPayloadJson = """{"transactionId":"FCC-$i","pumpNumber":${(i % 4) + 1},"amountMinorUnits":${((i % 100) + 10) * 500}}""",
                correlationId = UUID.randomUUID().toString(),
                uploadAttempts = if (syncStatus == "PENDING") 0 else 1,
                lastUploadAttemptAt = if (syncStatus == "PENDING") null else createdAt,
                lastUploadError = null,
                schemaVersion = 1,
                createdAt = createdAt,
                updatedAt = updatedAt,
            )
        }
    }

    /**
     * Generate [count] pre-auth records with representative status distribution.
     */
    fun preAuthRecords(
        count: Int = 100,
        baseTime: Instant = Instant.now(),
    ): List<PreAuthRecord> {
        val statuses = listOf("PENDING", "AUTHORIZED", "DISPENSING", "COMPLETED", "EXPIRED")
        return (0 until count).map { i ->
            val createdAt = baseTime.minusSeconds(i * 60L).toString()
            val status = statuses[i % statuses.size]
            PreAuthRecord(
                id = UUID.randomUUID().toString(),
                siteCode = SITES[i % SITES.size],
                odooOrderId = "ORDER-${i.toString().padStart(8, '0')}",
                pumpNumber = (i % 4) + 1,
                nozzleNumber = (i % 2) + 1,
                productCode = PRODUCTS[i % PRODUCTS.size],
                currencyCode = CURRENCIES[i % CURRENCIES.size],
                requestedAmountMinorUnits = ((i % 10) + 1) * 1000L,
                authorizedAmountMinorUnits = if (status == "PENDING") null else ((i % 10) + 1) * 1000L,
                status = status,
                fccCorrelationId = if (i % 5 == 0) null else "CORR-${i.toString().padStart(6, '0')}",
                fccAuthorizationCode = if (status == "AUTHORIZED" || status == "DISPENSING") "AUTH-$i" else null,
                failureReason = null,
                customerName = if (i % 3 == 0) null else "Customer $i",
                customerTaxId = null,
                rawFccResponse = null,
                requestedAt = createdAt,
                authorizedAt = if (status == "PENDING") null else createdAt,
                completedAt = if (status == "COMPLETED") createdAt else null,
                expiresAt = baseTime.plusSeconds(300L).toString(),
                isCloudSynced = if (status == "COMPLETED") 1 else 0,
                cloudSyncAttempts = 0,
                lastCloudSyncAttemptAt = null,
                schemaVersion = 1,
                createdAt = createdAt,
            )
        }
    }
}
