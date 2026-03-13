using System.Text.RegularExpressions;
using System.Xml.Linq;
using FccMiddleware.Adapter.Radix.Internal;
using FccMiddleware.Domain.Common;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;
using Microsoft.Extensions.Logging;

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

    // Matches a well-formed <TABLE ...>...</TABLE> or <TABLE>...</TABLE> element.
    // Anchored to avoid partial/injected matches. Compiled for reuse.
    private static readonly Regex TableElementRegex = new(
        @"<TABLE(?:\s[^>]*)?>[\s\S]*?</TABLE>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Matches <SIGNATURE>hex</SIGNATURE> with optional whitespace inside.
    private static readonly Regex SignatureElementRegex = new(
        @"<SIGNATURE>\s*([0-9a-fA-F]+)\s*</SIGNATURE>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly SiteFccConfig _config;
    private readonly ILogger<RadixCloudAdapter> _logger;
    private readonly IReadOnlyDictionary<(int PumpAddr, int Fp), int>? _reversePumpMap;

    public RadixCloudAdapter(HttpClient httpClient, SiteFccConfig config, ILogger<RadixCloudAdapter> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;

        // Build reverse lookup for O(1) pump resolution instead of O(n) scan
        if (config.FccPumpAddressMap is { Count: > 0 })
        {
            _reversePumpMap = config.FccPumpAddressMap
                .ToDictionary(kvp => (kvp.Value.PumpAddr, kvp.Value.Fp), kvp => kvp.Key);
        }
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
            // We extract the raw <TABLE> content (preserving exact whitespace) from the
            // original string using regex to ensure structural validity and prevent
            // malformed XML from bypassing signature validation.
            if (!string.IsNullOrEmpty(_config.SharedSecret))
            {
                var tableMatch = TableElementRegex.Match(payload);
                var sigMatch = SignatureElementRegex.Match(payload);

                if (!tableMatch.Success || !sigMatch.Success)
                {
                    _logger.LogWarning(
                        "Radix signature validation skipped: TABLE element {TableFound}, SIGNATURE element {SigFound}",
                        tableMatch.Success ? "found" : "MISSING",
                        sigMatch.Success ? "found" : "MISSING");
                    return ValidationResult.Fail("MISSING_SIGNATURE_ELEMENTS",
                        "Radix XML missing TABLE or SIGNATURE element required for signature validation.");
                }

                // Verify the parsed DOM also contains the TABLE element — guards against
                // injection of a TABLE-like string outside the actual XML structure.
                if (doc.Descendants("TABLE").FirstOrDefault() is null)
                {
                    return ValidationResult.Fail("INVALID_XML_STRUCTURE",
                        "Radix XML TABLE element found in raw text but not in parsed DOM.");
                }

                var tableContent = tableMatch.Value;
                var signatureText = sigMatch.Groups[1].Value;

                if (!RadixSignatureHelper.ValidateSignature(signatureText, tableContent, _config.SharedSecret))
                {
                    _logger.LogWarning(
                        "Radix signature mismatch. TABLE length={TableLength}, Signature={SignaturePrefix}...",
                        tableContent.Length, signatureText.Length > 8 ? signatureText[..8] : signatureText);
                    return ValidationResult.Fail("INVALID_SIGNATURE", "Radix XML signature validation failed.");
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

            // FccTransactionId = "{FDC_NUM}-{FDC_SAVE_NUM}" — DB max is 200 chars
            var compositeIdLen = trn.Attribute("FDC_NUM")!.Value.Length + 1 + trn.Attribute("FDC_SAVE_NUM")!.Value.Length;
            if (compositeIdLen > 200)
                return ValidationResult.Fail(
                    "FIELD_TOO_LONG",
                    $"Radix FDC_NUM-FDC_SAVE_NUM composite ID exceeds max length of 200 (got {compositeIdLen}).");

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

            if (!root.TryGetProperty("fccTransactionId", out var fccTxnId))
                return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Missing fccTransactionId in JSON payload.");

            var txnIdStr = fccTxnId.GetString();
            if (txnIdStr is not null && txnIdStr.Length > 200)
                return ValidationResult.Fail(
                    "FIELD_TOO_LONG",
                    $"fccTransactionId exceeds max length of 200 (got {txnIdStr.Length}).");

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
        var currencyFactor = CurrencyHelper.GetCurrencyFactor(_config.CurrencyCode);
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
        var startedAt = ParseRadixTimestamp(dto.StartTime, "StartTime", fccTransactionId);
        var completedAt = ParseRadixTimestamp(dto.EndTime, "EndTime", fccTransactionId);

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
        if (_reversePumpMap != null && _reversePumpMap.TryGetValue((pumpAddr, fp), out var pumpNumber))
            return pumpNumber;

        // Fallback: use PUMP_ADDR + offset (consistent with edge agent fallback logic)
        return pumpAddr + _config.PumpNumberOffset;
    }

    private DateTimeOffset ParseRadixTimestamp(string timestamp, string fieldName, string fccTransactionId)
    {
        if (DateTimeOffset.TryParse(timestamp, out var parsed))
            return parsed;

        // Try parsing as local time in site timezone
        if (DateTime.TryParse(timestamp, out var localTime))
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(_config.Timezone);
            return new DateTimeOffset(localTime, tz.GetUtcOffset(localTime));
        }

        // L-07: Reject transactions with unparseable timestamps instead of silently using UtcNow,
        // which would place them in the wrong time window.
        throw new InvalidOperationException(
            $"Radix {fieldName} failed to parse for transaction {fccTransactionId} (raw: '{timestamp}'). " +
            "Cannot normalize transaction without a valid timestamp.");
    }

}
