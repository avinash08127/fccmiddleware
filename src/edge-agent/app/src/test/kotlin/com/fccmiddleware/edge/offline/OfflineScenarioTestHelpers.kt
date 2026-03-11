package com.fccmiddleware.edge.offline

import com.fccmiddleware.edge.buffer.entity.BufferedTransaction
import com.fccmiddleware.edge.sync.CloudErrorResponse
import com.fccmiddleware.edge.sync.CloudUploadRecordResult
import com.fccmiddleware.edge.sync.CloudUploadResponse
import com.fccmiddleware.edge.sync.SyncedStatusResponse
import com.fccmiddleware.edge.sync.TransactionStatusEntry
import java.time.Instant
import java.time.temporal.ChronoUnit
import java.util.UUID

/**
 * Shared test helpers for offline scenario stress tests (EA-6.1).
 *
 * Provides factory methods for BufferedTransaction, CloudUploadResponse,
 * and SyncedStatusResponse used across OFF-1 through OFF-6 test classes.
 */
object OfflineScenarioTestHelpers {

    private val SITES = listOf("SITE_001", "SITE_002", "SITE_003")
    private val PRODUCTS = listOf("ULP95", "ULP93", "DIESEL", "LPG")

    /**
     * Create a single PENDING BufferedTransaction with deterministic timestamps.
     *
     * @param index Used to spread timestamps across a time window.
     * @param baseTime End of the time window.
     * @param windowSeconds Total time window span.
     */
    fun makeTransaction(
        index: Int = 0,
        baseTime: Instant = Instant.now(),
        windowSeconds: Long = ChronoUnit.DAYS.getDuration().seconds,
        siteCode: String = "SITE_001",
        syncStatus: String = "PENDING",
    ): BufferedTransaction {
        val createdAt = baseTime
            .minusSeconds(windowSeconds * index / 1000)
            .toString()
        val completedAt = baseTime
            .minusSeconds(windowSeconds * index / 1000)
            .minusSeconds(30)
            .toString()
        return BufferedTransaction(
            id = UUID.randomUUID().toString(),
            fccTransactionId = "FCC-${index.toString().padStart(8, '0')}",
            siteCode = siteCode,
            pumpNumber = (index % 4) + 1,
            nozzleNumber = (index % 2) + 1,
            productCode = PRODUCTS[index % PRODUCTS.size],
            volumeMicrolitres = ((index % 50) + 10) * 1_000_000L,
            amountMinorUnits = ((index % 100) + 10) * 500L,
            unitPriceMinorPerLitre = 1_500L + (index % 50),
            currencyCode = "MWK",
            startedAt = baseTime
                .minusSeconds(windowSeconds * index / 1000)
                .minusSeconds(90)
                .toString(),
            completedAt = completedAt,
            fiscalReceiptNumber = if (index % 5 == 0) null else "FISC-${index.toString().padStart(8, '0')}",
            fccVendor = "DOMS",
            attendantId = null,
            status = "PENDING",
            syncStatus = syncStatus,
            ingestionSource = "RELAY",
            rawPayloadJson = null,
            correlationId = UUID.randomUUID().toString(),
            uploadAttempts = 0,
            lastUploadAttemptAt = null,
            lastUploadError = null,
            schemaVersion = 1,
            createdAt = createdAt,
            updatedAt = createdAt,
        )
    }

    /**
     * Generate a batch of PENDING transactions.
     */
    fun makeBatch(
        count: Int,
        baseTime: Instant = Instant.now(),
        siteCode: String = "SITE_001",
    ): List<BufferedTransaction> =
        (0 until count).map { makeTransaction(index = it, baseTime = baseTime, siteCode = siteCode) }

    /**
     * Build a successful CloudUploadResponse where all records are ACCEPTED.
     */
    fun makeAllAcceptedResponse(
        batch: List<BufferedTransaction>,
    ): CloudUploadResponse = CloudUploadResponse(
        results = batch.map {
            CloudUploadRecordResult(
                fccTransactionId = it.fccTransactionId,
                siteCode = it.siteCode,
                outcome = "ACCEPTED",
                id = UUID.randomUUID().toString(),
            )
        },
        acceptedCount = batch.size,
        duplicateCount = 0,
        rejectedCount = 0,
    )

    /**
     * Build a partial success response: first [acceptedCount] are ACCEPTED,
     * remainder produce a transport error (simulated by not including them).
     */
    fun makePartialResponse(
        batch: List<BufferedTransaction>,
        acceptedCount: Int,
    ): CloudUploadResponse {
        val accepted = batch.take(acceptedCount).map {
            CloudUploadRecordResult(
                fccTransactionId = it.fccTransactionId,
                siteCode = it.siteCode,
                outcome = "ACCEPTED",
                id = UUID.randomUUID().toString(),
            )
        }
        val rejected = batch.drop(acceptedCount).map {
            CloudUploadRecordResult(
                fccTransactionId = it.fccTransactionId,
                siteCode = it.siteCode,
                outcome = "REJECTED",
                error = CloudErrorResponse("UPLOAD_INTERRUPTED", "Connection lost mid-batch"),
            )
        }
        return CloudUploadResponse(
            results = accepted + rejected,
            acceptedCount = acceptedCount,
            duplicateCount = 0,
            rejectedCount = batch.size - acceptedCount,
        )
    }

    /**
     * Build a SyncedStatusResponse confirming all IDs as SYNCED_TO_ODOO.
     */
    fun makeSyncedStatusResponse(
        fccTransactionIds: List<String>,
    ): SyncedStatusResponse = SyncedStatusResponse(
        statuses = fccTransactionIds.map {
            TransactionStatusEntry(id = it, status = "SYNCED_TO_ODOO")
        },
    )
}
