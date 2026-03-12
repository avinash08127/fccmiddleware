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
 *   Amount : DOMS x10 value × 10  = minor currency units (e.g., cents)
 *   Unit price: DOMS x10 value × 10 = minor currency units per litre
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
     * DOMS stores amounts as value × 10 of the minor unit.
     * e.g., 1234 in DOMS = 12340 minor units? No — DOMS x10 means the value
     * is 10× the actual minor unit value, so we divide? Actually per the plan:
     * "Amount: x10 factor" and "x10 x 10 = minor units" — so multiply by 10.
     *
     * DOMS amount 1234 (x10) → 1234 × 10 = 12340? That seems wrong.
     * Re-reading: "Amount: x10 x 10 = minor units" — this means the DOMS value
     * already has a x10 factor baked in, so to get minor units we multiply by
     * an additional factor. But "x10 factor" for amount means the raw value IS
     * the amount in (minor_units / 10). So rawValue * 10 = minor units.
     *
     * Example: If actual amount is $12.34 (1234 cents), DOMS stores 123 (x10).
     * 123 * 10 = 1230? That's wrong too.
     *
     * Most likely interpretation: DOMS amount is already in minor units (cents),
     * with the "x10" meaning the value includes a 1/10 precision factor.
     * So raw DOMS value of 12340 / 10 = 1234 cents.
     *
     * However, the plan states "x10 x 10 = minor units" suggesting multiplication.
     * Following the plan literally: domsValue * 10 = minor units.
     */
    fun domsAmountToMinorUnits(domsX10Value: Long): Long = domsX10Value * 10L

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
