using System.Text.Json;
using FccMiddleware.Adapter.Advatec.Internal;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Adapter.Advatec;

/// <summary>
/// Cloud-side Advatec FCC adapter implementing IFccAdapter.
///
/// Advatec is push-only via Receipt webhook. This adapter handles:
///   1. Receipt webhook JSON payload validation
///   2. Receipt normalization to CanonicalTransaction
///   3. Edge-uploaded canonical JSON passthrough
///
/// FetchTransactionsAsync returns empty — Advatec has no pull capability.
/// Tanzania-only: amounts are in TZS (0 decimal places).
/// </summary>
public sealed class AdvatecCloudAdapter : IFccAdapter
{
    private const string AdapterVer = "1.0.0";
    private const string ContentTypeJson = "application/json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SiteFccConfig _config;

    public AdvatecCloudAdapter(SiteFccConfig config)
    {
        _config = config;
    }

    // ── NormalizeTransaction ─────────────────────────────────────────────────

    /// <inheritdoc />
    public CanonicalTransaction NormalizeTransaction(RawPayloadEnvelope rawPayload)
    {
        var envelope = JsonSerializer.Deserialize<AdvatecWebhookEnvelope>(
            rawPayload.Payload, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize Advatec webhook payload.");

        if (envelope.Data is null)
            throw new InvalidOperationException("Advatec webhook payload has no Data.");

        return MapToCanonical(envelope.Data, rawPayload.SiteCode);
    }

    // ── ValidatePayload ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult ValidatePayload(RawPayloadEnvelope rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload.Payload))
            return ValidationResult.Fail("NULL_PAYLOAD", "Payload is null or empty.");

        if (rawPayload.Vendor != FccVendor.ADVATEC)
            return ValidationResult.Fail(
                "VENDOR_MISMATCH",
                $"Expected vendor ADVATEC but received {rawPayload.Vendor}.");

        if (!string.Equals(rawPayload.ContentType, ContentTypeJson, StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Fail(
                "UNSUPPORTED_MESSAGE_TYPE",
                $"Advatec adapter requires application/json, got '{rawPayload.ContentType}'.");

        AdvatecWebhookEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AdvatecWebhookEnvelope>(rawPayload.Payload, JsonOpts)!;
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail("INVALID_JSON", $"JSON parse error: {ex.Message}");
        }

        if (!string.Equals(envelope.DataType, "Receipt", StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Fail(
                "UNSUPPORTED_MESSAGE_TYPE",
                $"Advatec DataType '{envelope.DataType}' is not 'Receipt'.");

        if (envelope.Data is null)
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Advatec payload missing Data.");

        if (string.IsNullOrWhiteSpace(envelope.Data.TransactionId))
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Advatec Receipt missing TransactionId.");

        if (envelope.Data.Items is null || envelope.Data.Items.Count == 0)
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Advatec Receipt has no Items.");

        if (envelope.Data.AmountInclusive <= 0)
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Advatec AmountInclusive must be > 0.");

        return ValidationResult.Ok();
    }

    // ── FetchTransactionsAsync ───────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TransactionBatch> FetchTransactionsAsync(
        FetchCursor cursor,
        CancellationToken cancellationToken = default)
    {
        // Advatec is push-only via Receipt webhook. No pull capability.
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
        Vendor = FccVendor.ADVATEC,
        AdapterVersion = AdapterVer,
        SupportedIngestionMethods = [IngestionMethod.PUSH],
        SupportsPreAuth = false,
        SupportsPumpStatus = false,
        Protocol = "REST_JSON"
    };

    // ── Private: mapping ─────────────────────────────────────────────────────

    private CanonicalTransaction MapToCanonical(AdvatecReceiptData receipt, string siteCode)
    {
        var item = receipt.Items![0];

        // Dedup key: siteCode-TransactionId
        var fccTransactionId = $"{siteCode}-{receipt.TransactionId}";

        // Volume: Quantity litres * 1,000,000 = microlitres (decimal, no float)
        var volumeMicrolitres = (long)(item.Quantity * 1_000_000m);

        // Amount: AmountInclusive * currency factor = minor units
        var currencyFactor = GetCurrencyFactor(_config.CurrencyCode);
        var amountMinorUnits = (long)(receipt.AmountInclusive * currencyFactor);

        // Unit price: Item.Price * currency factor = minor per litre
        var unitPriceMinor = (long)(item.Price * currencyFactor);

        // Product code mapping
        var rawProduct = item.Product ?? "UNKNOWN";
        var productCode = _config.ProductCodeMapping.TryGetValue(rawProduct, out var mapped)
            ? mapped
            : rawProduct;

        // Timestamps: Date + Time with configured timezone -> UTC
        var completedAt = ParseAdvatecTimestamp(receipt.Date, receipt.Time, _config.Timezone)
            ?? DateTimeOffset.UtcNow;
        var startedAt = completedAt; // Only one timestamp available

        return new CanonicalTransaction
        {
            FccTransactionId = fccTransactionId,
            SiteCode = siteCode,
            PumpNumber = 0 - _config.PumpNumberOffset, // AQ-3: pump not in receipt
            NozzleNumber = 1, // AQ-9: no nozzle concept
            ProductCode = productCode,
            VolumeMicrolitres = volumeMicrolitres,
            AmountMinorUnits = amountMinorUnits,
            UnitPriceMinorPerLitre = unitPriceMinor,
            CurrencyCode = _config.CurrencyCode,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            FccVendor = FccVendor.ADVATEC,
            FiscalReceiptNumber = receipt.ReceiptCode,
        };
    }

    private static DateTimeOffset? ParseAdvatecTimestamp(
        string? date, string? time, string timezone)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        try
        {
            var localDate = DateOnly.ParseExact(date, "yyyy-MM-dd");
            var localTime = !string.IsNullOrWhiteSpace(time)
                ? TimeOnly.ParseExact(time, "HH:mm:ss")
                : TimeOnly.MinValue;

            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var dateTime = new DateTime(localDate, localTime, DateTimeKind.Unspecified);
            var offset = tz.GetUtcOffset(dateTime);
            return new DateTimeOffset(dateTime, offset).ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static decimal GetCurrencyFactor(string currencyCode)
    {
        return currencyCode.ToUpperInvariant() switch
        {
            "KWD" or "BHD" or "OMR" => 1000m,
            "JPY" or "KRW" or "TZS" or "UGX" or "RWF" => 1m,
            _ => 100m
        };
    }
}
