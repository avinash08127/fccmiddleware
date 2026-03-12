using System.Globalization;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms.Model;

namespace FccDesktopAgent.Core.Adapter.Doms.Mapping;

/// <summary>
/// Maps DOMS protocol data to canonical transaction format.
///
/// Conversion rules (NO floating-point):
///   Volume : centilitres x 10,000 = microlitres (1 cL = 10,000 uL)
///   Amount : DOMS x10 value x 10  = minor currency units (e.g., cents)
///   Unit price: DOMS x10 value x 10 = minor currency units per litre
///   Timestamp: "yyyyMMddHHmmss" in site local time -> UTC DateTimeOffset
///   Pump number: fpId + pumpNumberOffset
///   Product code: raw code -> canonical via productCodeMapping (fallback: raw)
/// </summary>
public static class DomsCanonicalMapper
{
    private const string DomsTimestampFormat = "yyyyMMddHHmmss";

    /// <summary>
    /// Convert a DOMS transaction DTO to a canonical transaction.
    /// </summary>
    /// <param name="dto">Raw DOMS transaction from supervised buffer.</param>
    /// <param name="siteCode">Site identifier.</param>
    /// <param name="legalEntityId">Legal entity owning the site.</param>
    /// <param name="currencyCode">ISO 4217 currency code.</param>
    /// <param name="timezone">IANA timezone for the site (e.g. "Africa/Johannesburg").</param>
    /// <param name="pumpNumberOffset">Offset added to raw FCC pump numbers.</param>
    /// <param name="productCodeMapping">Optional mapping from raw FCC product codes to canonical codes.</param>
    /// <returns>A canonical transaction, or null if mapping fails.</returns>
    public static CanonicalTransaction? MapToCanonical(
        DomsJplTransactionDto dto,
        string siteCode,
        string legalEntityId,
        string currencyCode,
        string timezone,
        int pumpNumberOffset,
        IReadOnlyDictionary<string, string>? productCodeMapping = null)
    {
        try
        {
            var volumeMicrolitres = CentilitresToMicrolitres(dto.VolumeCl);
            var amountMinorUnits = DomsAmountToMinorUnits(dto.AmountX10);
            var unitPriceMinor = DomsAmountToMinorUnits(dto.UnitPriceX10);

            var completedAtUtc = DomsTimestampToUtc(dto.Timestamp, timezone);
            if (completedAtUtc is null)
                return null;

            var pumpNumber = dto.FpId + pumpNumberOffset;

            var productCode = dto.ProductCode;
            if (productCodeMapping is not null &&
                productCodeMapping.TryGetValue(dto.ProductCode, out var mappedCode))
            {
                productCode = mappedCode;
            }

            var now = DateTimeOffset.UtcNow;

            return new CanonicalTransaction
            {
                Id = Guid.NewGuid().ToString(),
                FccTransactionId = dto.TransactionId,
                SiteCode = siteCode,
                PumpNumber = pumpNumber,
                NozzleNumber = dto.NozzleId,
                ProductCode = productCode,
                VolumeMicrolitres = volumeMicrolitres,
                AmountMinorUnits = amountMinorUnits,
                UnitPriceMinorPerLitre = unitPriceMinor,
                CurrencyCode = currencyCode,
                StartedAt = completedAtUtc.Value, // DOMS provides single timestamp
                CompletedAt = completedAtUtc.Value,
                FccVendor = "DOMS",
                LegalEntityId = legalEntityId,
                AttendantId = dto.AttendantId,
                Status = TransactionStatus.Pending,
                IngestionSource = nameof(IngestionSource.EdgeUpload),
                IngestedAt = now,
                UpdatedAt = now,
                SchemaVersion = "1.0",
                IsDuplicate = false,
                CorrelationId = Guid.NewGuid().ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Convert centilitres to microlitres.
    /// 1 cL = 0.01 L = 10,000 uL
    /// </summary>
    public static long CentilitresToMicrolitres(long centilitres) => centilitres * 10_000L;

    /// <summary>
    /// Convert DOMS x10 amount to minor currency units.
    /// DOMS stores amounts as value / 10 of the minor unit.
    /// rawValue * 10 = minor units.
    /// </summary>
    public static long DomsAmountToMinorUnits(long domsX10Value) => domsX10Value * 10L;

    /// <summary>
    /// Parse DOMS local timestamp and convert to UTC DateTimeOffset.
    /// </summary>
    /// <param name="domsTimestamp">Format: "yyyyMMddHHmmss".</param>
    /// <param name="timezone">IANA timezone (e.g., "Africa/Johannesburg").</param>
    /// <returns>UTC DateTimeOffset, or null if parsing fails.</returns>
    public static DateTimeOffset? DomsTimestampToUtc(string domsTimestamp, string timezone)
    {
        try
        {
            if (!DateTime.TryParseExact(
                    domsTimestamp,
                    DomsTimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var localDateTime))
            {
                return null;
            }

            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var offset = tz.GetUtcOffset(localDateTime);
            var dto = new DateTimeOffset(localDateTime, offset);
            return dto.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }
}
