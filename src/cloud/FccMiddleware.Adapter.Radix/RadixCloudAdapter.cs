using System.Xml.Linq;
using FccMiddleware.Adapter.Radix.Internal;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Adapter.Radix;

/// <summary>
/// Cloud-side Radix FCC adapter implementing IFccAdapter.
///
/// Supports two payload paths:
///   1. Canonical JSON from edge agent (pre-normalized, content-type: application/json)
///   2. Raw XML from CLOUD_DIRECT mode (content-type: text/xml) — parsed and normalized here
///
/// Validates Radix-specific signatures when raw XML is received.
/// </summary>
public sealed class RadixCloudAdapter : IFccAdapter
{
    private const string AdapterVer = "1.0.0";
    private const string ContentTypeJson = "application/json";
    private const string ContentTypeXml = "text/xml";

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly SiteFccConfig _config;

    public RadixCloudAdapter(HttpClient httpClient, SiteFccConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    // ── NormalizeTransaction ─────────────────────────────────────────────────

    /// <inheritdoc />
    public CanonicalTransaction NormalizeTransaction(RawPayloadEnvelope rawPayload)
    {
        if (string.Equals(rawPayload.ContentType, ContentTypeXml, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeFromXml(rawPayload);
        }

        // Canonical JSON from edge agent
        return NormalizeFromJson(rawPayload);
    }

    // ── ValidatePayload ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult ValidatePayload(RawPayloadEnvelope rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload.Payload))
            return ValidationResult.Fail("NULL_PAYLOAD", "Payload is null or empty.");

        if (rawPayload.Vendor != FccVendor.RADIX)
            return ValidationResult.Fail(
                "VENDOR_MISMATCH",
                $"Expected vendor RADIX but received {rawPayload.Vendor}.");

        if (string.Equals(rawPayload.ContentType, ContentTypeXml, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateXmlPayload(rawPayload.Payload);
        }

        if (string.Equals(rawPayload.ContentType, ContentTypeJson, StringComparison.OrdinalIgnoreCase))
        {
            return ValidateJsonPayload(rawPayload.Payload);
        }

        return ValidationResult.Fail(
            "UNSUPPORTED_MESSAGE_TYPE",
            $"Radix adapter supports application/json or text/xml, got '{rawPayload.ContentType}'.");
    }

    // ── FetchTransactionsAsync ───────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TransactionBatch> FetchTransactionsAsync(
        FetchCursor cursor,
        CancellationToken cancellationToken = default)
    {
        // Cloud Radix adapter supports PUSH-only in MVP.
        // Pull-mode would require direct HTTP/XML communication with the FDC,
        // which is handled by the edge agent.
        return Task.FromResult(new TransactionBatch
        {
            Transactions = Array.Empty<CanonicalTransaction>(),
            HasMore = false,
        });
    }

    // ── GetAdapterMetadata ───────────────────────────────────────────────────

    /// <inheritdoc />
    public AdapterInfo GetAdapterMetadata() => new()
    {
        Vendor = FccVendor.RADIX,
        AdapterVersion = AdapterVer,
        SupportedIngestionMethods = [IngestionMethod.PUSH],
        SupportsPreAuth = false,
        SupportsPumpStatus = false,
        Protocol = "HTTP_XML"
    };

    // ── Private: XML normalization ───────────────────────────────────────────

    private CanonicalTransaction NormalizeFromXml(RawPayloadEnvelope rawPayload)
    {
        var doc = XDocument.Parse(rawPayload.Payload);
        var trnElement = doc.Root?.Element("TRN")
            ?? throw new InvalidOperationException("Radix XML missing TRN element.");

        var dto = RadixTransactionDto.FromXml(trnElement);
        return MapToCanonical(dto, rawPayload.SiteCode);
    }

    private CanonicalTransaction NormalizeFromJson(RawPayloadEnvelope rawPayload)
    {
        // Edge-uploaded canonical JSON — already in canonical shape
        var dto = System.Text.Json.JsonSerializer.Deserialize<CanonicalTransaction>(
            rawPayload.Payload, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize Radix JSON payload.");

        return dto;
    }

    private ValidationResult ValidateXmlPayload(string payload)
    {
        try
        {
            var doc = XDocument.Parse(payload);

            // Validate signature if shared secret is configured
            if (!string.IsNullOrEmpty(_config.SharedSecret))
            {
                var signatureElement = doc.Root?.Element("SIGNATURE")
                    ?? doc.Root?.Element("TRN")?.Parent?.Element("SIGNATURE");

                if (signatureElement != null)
                {
                    var signatureContent = GetContentWithoutSignature(doc);
                    if (!RadixSignatureHelper.ValidateSignature(
                        signatureElement.Value, signatureContent, _config.SharedSecret))
                    {
                        return ValidationResult.Fail("INVALID_SIGNATURE", "Radix XML signature validation failed.");
                    }
                }
            }

            var trn = doc.Root?.Element("TRN");
            if (trn == null)
                return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Radix XML missing TRN element.");

            if (string.IsNullOrWhiteSpace(trn.Element("FDC_NUM")?.Value))
                return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Radix TRN missing FDC_NUM.");

            if (string.IsNullOrWhiteSpace(trn.Element("FDC_SAVE_NUM")?.Value))
                return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Radix TRN missing FDC_SAVE_NUM.");

            return ValidationResult.Ok();
        }
        catch (Exception ex)
        {
            return ValidationResult.Fail("INVALID_XML", $"Radix XML parse error: {ex.Message}");
        }
    }

    private static ValidationResult ValidateJsonPayload(string payload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (!root.TryGetProperty("fccTransactionId", out _))
                return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Missing fccTransactionId in JSON payload.");

            return ValidationResult.Ok();
        }
        catch (System.Text.Json.JsonException ex)
        {
            return ValidationResult.Fail("INVALID_JSON", $"JSON parse error: {ex.Message}");
        }
    }

    // ── Private: mapping ─────────────────────────────────────────────────────

    private CanonicalTransaction MapToCanonical(RadixTransactionDto dto, string siteCode)
    {
        // Dedup key: FDC_NUM-FDC_SAVE_NUM
        var fccTransactionId = $"{dto.FdcNum}-{dto.FdcSaveNum}";

        // Volume: litres × 1,000,000 = microlitres (via decimal, no float)
        var volumeMicrolitres = (long)(decimal.Parse(dto.Volume) * 1_000_000m);

        // Amount: decimal × currency factor = minor units
        var currencyFactor = GetCurrencyFactor(_config.CurrencyCode);
        var amountMinorUnits = (long)(decimal.Parse(dto.Amount) * currencyFactor);

        // Unit price: decimal × currency factor = minor per litre
        var unitPriceMinor = (long)(decimal.Parse(dto.UnitPrice) * currencyFactor);

        // Pump mapping: resolve PUMP_ADDR/FP to canonical pump number
        var canonicalPumpNumber = ResolvePumpNumber(dto.PumpAddr, dto.Fp);

        // Product code mapping
        var productCode = _config.ProductCodeMapping.TryGetValue(dto.ProductCode, out var mapped)
            ? mapped
            : dto.ProductCode;

        // Timestamps: parse FDC local time → UTC
        var startedAt = ParseRadixTimestamp(dto.StartTime);
        var completedAt = ParseRadixTimestamp(dto.EndTime);

        return new CanonicalTransaction
        {
            FccTransactionId = fccTransactionId,
            SiteCode = siteCode,
            PumpNumber = canonicalPumpNumber,
            NozzleNumber = dto.Nozzle,
            ProductCode = productCode,
            VolumeMicrolitres = volumeMicrolitres,
            AmountMinorUnits = amountMinorUnits,
            UnitPriceMinorPerLitre = unitPriceMinor,
            CurrencyCode = _config.CurrencyCode,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            FccVendor = FccVendor.RADIX,
            FiscalReceiptNumber = dto.EfdId,
        };
    }

    private int ResolvePumpNumber(int pumpAddr, int fp)
    {
        if (_config.FccPumpAddressMap != null)
        {
            foreach (var kvp in _config.FccPumpAddressMap)
            {
                if (kvp.Value.PumpAddr == pumpAddr && kvp.Value.Fp == fp)
                    return kvp.Key;
            }
        }
        // Fallback: use FP as pump number with offset
        return fp - _config.PumpNumberOffset;
    }

    private DateTimeOffset ParseRadixTimestamp(string timestamp)
    {
        if (DateTimeOffset.TryParse(timestamp, out var parsed))
            return parsed;

        // Try parsing as local time in site timezone
        if (DateTime.TryParse(timestamp, out var localTime))
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(_config.Timezone);
            return new DateTimeOffset(localTime, tz.GetUtcOffset(localTime));
        }

        return DateTimeOffset.UtcNow;
    }

    private static decimal GetCurrencyFactor(string currencyCode)
    {
        // Most currencies have 2 decimal places (100 minor units per major)
        return currencyCode.ToUpperInvariant() switch
        {
            "KWD" or "BHD" or "OMR" => 1000m,  // 3 decimal places
            "JPY" or "KRW" => 1m,               // 0 decimal places
            _ => 100m                            // 2 decimal places (default)
        };
    }

    private static string GetContentWithoutSignature(XDocument doc)
    {
        var clone = new XDocument(doc);
        clone.Root?.Element("SIGNATURE")?.Remove();
        return clone.Root?.ToString(SaveOptions.DisableFormatting) ?? "";
    }
}
