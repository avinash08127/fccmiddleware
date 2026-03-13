package com.fccmiddleware.edge.adapter.doms.mapping

import com.fccmiddleware.edge.adapter.common.*
import com.fccmiddleware.edge.adapter.doms.model.DomsTransactionDto
import java.time.LocalDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.UUID

/**
 * Maps DOMS protocol data to canonical transaction format.
 *
 * Conversion rules (NO floating-point):
 *   Volume : centilitres × 10,000 = microlitres (1 cL = 10,000 µL)
 *   Amount : DOMS x10 value / 10  = minor currency units (e.g., cents)
 *   Unit price: DOMS x10 value / 10 = minor currency units per litre
 *   Timestamp: "yyyyMMddHHmmss" in site local time → UTC ISO 8601
 *   Pump number: fpId + pumpNumberOffset
 *   Product code: raw code → canonical via productCodeMapping (fallback: raw)
 */
object DomsCanonicalMapper {

    private val DOMS_TIMESTAMP_FORMAT = DateTimeFormatter.ofPattern("yyyyMMddHHmmss")

    /**
     * Convert a DOMS transaction DTO to a canonical transaction.
     *
     * @param dto Raw DOMS transaction from supervised buffer.
     * @param config Agent FCC config with timezone, currency, product mapping, offset.
     * @param siteCode Site identifier.
     * @param legalEntityId Legal entity owning the site.
     * @return NormalizationResult.Success or NormalizationResult.Failure.
     */
    fun mapToCanonical(
        dto: DomsTransactionDto,
        config: AgentFccConfig,
        siteCode: String,
        legalEntityId: String,
    ): NormalizationResult {
        return try {
            val volumeMicrolitres = centilitresToMicrolitres(dto.volumeCl)
            val amountMinorUnits = domsAmountToMinorUnits(dto.amountX10)
            val unitPriceMinor = domsAmountToMinorUnits(dto.unitPriceX10)

            val completedAtUtc = domsTimestampToUtc(dto.timestamp, config.timezone)
                ?: return NormalizationResult.Failure(
                    errorCode = "MALFORMED_FIELD",
                    message = "Cannot parse DOMS timestamp '${dto.timestamp}' with timezone '${config.timezone}'",
                    fieldName = "timestamp",
                )

            val pumpNumber = dto.fpId + config.pumpNumberOffset
            val productCode = config.productCodeMapping[dto.productCode] ?: dto.productCode

            val now = java.time.Instant.now().toString()

            NormalizationResult.Success(
                CanonicalTransaction(
                    id = UUID.randomUUID().toString(),
                    fccTransactionId = dto.transactionId,
                    siteCode = siteCode,
                    pumpNumber = pumpNumber,
                    nozzleNumber = dto.nozzleId,
                    productCode = productCode,
                    volumeMicrolitres = volumeMicrolitres,
                    amountMinorUnits = amountMinorUnits,
                    unitPriceMinorPerLitre = unitPriceMinor,
                    startedAt = completedAtUtc, // DOMS provides single timestamp
                    completedAt = completedAtUtc,
                    fccVendor = FccVendor.DOMS,
                    legalEntityId = legalEntityId,
                    currencyCode = config.currencyCode,
                    status = TransactionStatus.PENDING,
                    ingestionSource = IngestionSource.EDGE_UPLOAD,
                    ingestedAt = now,
                    updatedAt = now,
                    schemaVersion = 1,
                    isDuplicate = false,
                    correlationId = UUID.randomUUID().toString(),
                    attendantId = dto.attendantId,
                )
            )
        } catch (e: Exception) {
            NormalizationResult.Failure(
                errorCode = "INVALID_PAYLOAD",
                message = "DOMS mapping failed: ${e.message}",
            )
        }
    }

    /**
     * Convert centilitres to microlitres.
     * 1 cL = 0.01 L = 10,000 µL
     */
    fun centilitresToMicrolitres(centilitres: Long): Long = centilitres * 10_000L

    /**
     * Convert DOMS x10 amount to minor currency units.
     *
     * DOMS FcSupParam stores amounts as (minor_currency_units × 10).
     * The "x10" suffix means the wire value is 10× the actual minor unit value.
     * To recover minor units: divide by 10.
     *
     * Example: $12.34 = 1234 cents. DOMS stores 12340 (= 1234 × 10).
     *          12340 / 10 = 1234 cents.
     */
    fun domsAmountToMinorUnits(domsX10Value: Long): Long = domsX10Value / 10L

    /**
     * Parse DOMS local timestamp and convert to UTC ISO 8601 string.
     *
     * @param domsTimestamp Format: "yyyyMMddHHmmss"
     * @param timezone IANA timezone (e.g., "Africa/Johannesburg")
     * @return UTC ISO 8601 string, or null if parsing fails.
     */
    fun domsTimestampToUtc(domsTimestamp: String, timezone: String): String? {
        return try {
            val localDateTime = LocalDateTime.parse(domsTimestamp, DOMS_TIMESTAMP_FORMAT)
            val zoneId = ZoneId.of(timezone)
            val zonedDateTime = localDateTime.atZone(zoneId)
            zonedDateTime.toInstant().toString()
        } catch (_: Exception) {
            null
        }
    }
}
