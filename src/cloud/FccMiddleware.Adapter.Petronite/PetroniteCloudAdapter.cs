using System.Text.Json;
using FccMiddleware.Adapter.Petronite.Internal;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Adapter.Petronite;

/// <summary>
/// Cloud-side Petronite FCC adapter implementing IFccAdapter.
///
/// Petronite is push-only via webhook. This adapter handles:
///   1. Webhook JSON payload validation and normalization
///   2. Edge-uploaded canonical JSON passthrough
///
/// FetchTransactionsAsync returns empty — Petronite has no pull capability.
/// </summary>
public sealed class PetroniteCloudAdapter : IFccAdapter
{
    private const string AdapterVer = "1.0.0";
    private const string ContentTypeJson = "application/json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SiteFccConfig _config;

    public PetroniteCloudAdapter(SiteFccConfig config)
    {
        _config = config;
    }

    // ── NormalizeTransaction ─────────────────────────────────────────────────

    /// <inheritdoc />
    public CanonicalTransaction NormalizeTransaction(RawPayloadEnvelope rawPayload)
    {
        var payload = JsonSerializer.Deserialize<PetroniteWebhookPayload>(
            rawPayload.Payload, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize Petronite webhook payload.");

        if (payload.Transaction == null)
            throw new InvalidOperationException("Petronite webhook payload has no transaction data.");

        return MapToCanonical(payload.Transaction, rawPayload.SiteCode);
    }

    // ── ValidatePayload ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public ValidationResult ValidatePayload(RawPayloadEnvelope rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload.Payload))
            return ValidationResult.Fail("NULL_PAYLOAD", "Payload is null or empty.");

        if (rawPayload.Vendor != FccVendor.PETRONITE)
            return ValidationResult.Fail(
                "VENDOR_MISMATCH",
                $"Expected vendor PETRONITE but received {rawPayload.Vendor}.");

        if (!string.Equals(rawPayload.ContentType, ContentTypeJson, StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Fail(
                "UNSUPPORTED_MESSAGE_TYPE",
                $"Petronite adapter requires application/json, got '{rawPayload.ContentType}'.");

        PetroniteWebhookPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<PetroniteWebhookPayload>(rawPayload.Payload, JsonOpts)!;
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail("INVALID_JSON", $"JSON parse error: {ex.Message}");
        }

        if (payload.EventType != "transaction.completed")
            return ValidationResult.Fail(
                "UNSUPPORTED_MESSAGE_TYPE",
                $"Petronite event type '{payload.EventType}' is not a transaction completion.");

        if (payload.Transaction == null)
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Petronite payload missing transaction data.");

        if (string.IsNullOrWhiteSpace(payload.Transaction.OrderId))
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Petronite transaction missing orderId.");

        if (payload.Transaction.VolumeLitres <= 0)
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Petronite volumeLitres must be > 0.");

        if (payload.Transaction.AmountMajor <= 0)
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Petronite amountMajor must be > 0.");

        return ValidationResult.Ok();
    }

    // ── FetchTransactionsAsync ───────────────────────────────────────────────

    /// <inheritdoc />
    public Task<TransactionBatch> FetchTransactionsAsync(
        FetchCursor cursor,
        CancellationToken cancellationToken = default)
    {
        // Petronite is push-only via webhook. No pull capability.
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
        Vendor = FccVendor.PETRONITE,
        AdapterVersion = AdapterVer,
        SupportedIngestionMethods = [IngestionMethod.PUSH],
        SupportsPreAuth = false,
        SupportsPumpStatus = false,
        Protocol = "REST_JSON"
    };

    // ── Private: mapping ─────────────────────────────────────────────────────

    private CanonicalTransaction MapToCanonical(PetroniteTransactionDto dto, string siteCode)
    {
        // Dedup key: siteCode-orderId
        var fccTransactionId = $"{siteCode}-{dto.OrderId}";

        // Volume: litres × 1,000,000 = microlitres (via decimal, no float)
        var volumeMicrolitres = (long)(dto.VolumeLitres * 1_000_000m);

        // Amount: major units × currency factor = minor units
        var currencyFactor = GetCurrencyFactor(_config.CurrencyCode);
        var amountMinorUnits = (long)(dto.AmountMajor * currencyFactor);

        // Unit price: major units × currency factor = minor per litre
        var unitPriceMinor = (long)(dto.UnitPrice * currencyFactor);

        // Product code mapping
        var productCode = _config.ProductCodeMapping.TryGetValue(dto.ProductCode, out var mapped)
            ? mapped
            : dto.ProductCode;

        // Timestamps
        var startedAt = DateTimeOffset.TryParse(dto.StartTime, out var start)
            ? start
            : DateTimeOffset.UtcNow;
        var completedAt = DateTimeOffset.TryParse(dto.EndTime, out var end)
            ? end
            : DateTimeOffset.UtcNow;

        return new CanonicalTransaction
        {
            FccTransactionId = fccTransactionId,
            SiteCode = siteCode,
            PumpNumber = dto.PumpNumber - _config.PumpNumberOffset,
            NozzleNumber = dto.NozzleNumber,
            ProductCode = productCode,
            VolumeMicrolitres = volumeMicrolitres,
            AmountMinorUnits = amountMinorUnits,
            UnitPriceMinorPerLitre = unitPriceMinor,
            CurrencyCode = _config.CurrencyCode,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            FccVendor = FccVendor.PETRONITE,
            FiscalReceiptNumber = dto.ReceiptCode,
            AttendantId = dto.AttendantId,
        };
    }

    private static decimal GetCurrencyFactor(string currencyCode)
    {
        return currencyCode.ToUpperInvariant() switch
        {
            "KWD" or "BHD" or "OMR" => 1000m,
            "JPY" or "KRW" => 1m,
            _ => 100m
        };
    }
}
