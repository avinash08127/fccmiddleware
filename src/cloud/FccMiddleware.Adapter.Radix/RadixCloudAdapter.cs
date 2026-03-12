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

        // TRN is nested inside <FDC_RESP><TABLE><ANS><TRN .../>> — use Descendants
        var trnElement = doc.Descendants("TRN").FirstOrDefault()
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

            // Validate signature if shared secret is configured.
            // Radix signs the <TABLE>...</TABLE> element: SHA1(<TABLE>...</TABLE> + secret).
            // We must extract the raw <TABLE> content (preserving exact whitespace) from the
            // original string, not from the parsed DOM, to match the character-exact hash.
            if (!string.IsNullOrEmpty(_config.SharedSecret))
            {
                var tableContent = ExtractRawElement(payload, "TABLE");
                var signatureText = ExtractRawElementText(payload, "SIGNATURE");

                if (tableContent != null && signatureText != null)
                {
                    if (!RadixSignatureHelper.ValidateSignature(
                        signatureText, tableContent, _config.SharedSecret))
                    {
                        return ValidationResult.Fail("INVALID_SIGNATURE", "Radix XML signature validation failed.");
                    }
                }
            }

            // TRN is nested inside <FDC_RESP><TABLE><ANS><TRN .../>> — use Descendants.
            // Radix sends TRN fields as XML attributes, not child elements.
            var trn = doc.Descendants("TRN").FirstOrDefault();
            if (trn == null)
                return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Radix XML missing TRN element.");

            if (string.IsNullOrWhiteSpace(trn.Attribute("FDC_NUM")?.Value))
                return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Radix TRN missing FDC_NUM.");

            if (string.IsNullOrWhiteSpace(trn.Attribute("FDC_SAVE_NUM")?.Value))
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
        // Fallback: use PUMP_ADDR + offset (consistent with edge agent fallback logic)
        return pumpAddr + _config.PumpNumberOffset;
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
        // Edge agents always normalize with factor 100. The cloud adapter must use
        // the same factor to avoid double-normalization or factor mismatch when
        // raw XML arrives via CLOUD_DIRECT mode.
        return 100m;
    }

    /// <summary>
    /// Extracts raw element text including its tags from the XML string.
    /// Returns the substring from &lt;tagName through &lt;/tagName&gt; inclusive,
    /// preserving exact whitespace for signature validation.
    /// </summary>
    private static string? ExtractRawElement(string xml, string tagName)
    {
        var openTag = $"<{tagName}";
        var closeTag = $"</{tagName}>";
        var startIdx = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (startIdx < 0) return null;

        var endIdx = xml.IndexOf(closeTag, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;

        return xml.Substring(startIdx, endIdx - startIdx + closeTag.Length);
    }

    /// <summary>
    /// Extracts the text content between &lt;tagName&gt; and &lt;/tagName&gt;,
    /// trimming whitespace.
    /// </summary>
    private static string? ExtractRawElementText(string xml, string tagName)
    {
        var openTag = $"<{tagName}>";
        var closeTag = $"</{tagName}>";
        var startIdx = xml.IndexOf(openTag, StringComparison.Ordinal);
        if (startIdx < 0) return null;

        var endIdx = xml.IndexOf(closeTag, startIdx, StringComparison.Ordinal);
        if (endIdx < 0) return null;

        return xml.Substring(startIdx + openTag.Length, endIdx - startIdx - openTag.Length).Trim();
    }
}
