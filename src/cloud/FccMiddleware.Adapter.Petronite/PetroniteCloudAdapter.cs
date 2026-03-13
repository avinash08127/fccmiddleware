using System.Text.Json;
using FccMiddleware.Adapter.Petronite.Internal;
using FccMiddleware.Domain.Common;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<PetroniteCloudAdapter> _logger;

    public PetroniteCloudAdapter(SiteFccConfig config, ILogger<PetroniteCloudAdapter> logger)
    {
        _config = config;
        _logger = logger;
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

        // FccTransactionId = "{siteCode}-{orderId}" — DB max is 200 chars
        if (payload.Transaction.OrderId.Length > 200)
            return ValidationResult.Fail(
                "FIELD_TOO_LONG",
                $"Petronite orderId exceeds max length of 200 (got {payload.Transaction.OrderId.Length}).");

        if (string.IsNullOrWhiteSpace(payload.Transaction.ProductCode))
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Petronite transaction missing productCode.");

        if (payload.Transaction.ProductCode.Length > 50)
            return ValidationResult.Fail(
                "FIELD_TOO_LONG",
                $"Petronite productCode exceeds max length of 50 (got {payload.Transaction.ProductCode.Length}).");

        if (payload.Transaction.VolumeLitres <= 0)
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Petronite volumeLitres must be > 0.");

        if (payload.Transaction.AmountMajor <= 0)
            return ValidationResult.Fail("MISSING_REQUIRED_FIELD", "Petronite amountMajor must be > 0.");

        if (!DateTimeOffset.TryParse(payload.Transaction.StartTime, out _))
            return ValidationResult.Fail(
                "INVALID_TIMESTAMP",
                $"Petronite StartTime '{payload.Transaction.StartTime}' is not a valid timestamp.");

        if (!DateTimeOffset.TryParse(payload.Transaction.EndTime, out _))
            return ValidationResult.Fail(
                "INVALID_TIMESTAMP",
                $"Petronite EndTime '{payload.Transaction.EndTime}' is not a valid timestamp.");

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
        var currencyFactor = CurrencyHelper.GetCurrencyFactor(_config.CurrencyCode);
        var amountMinorUnits = (long)(dto.AmountMajor * currencyFactor);

        // Unit price: major units × currency factor = minor per litre
        var unitPriceMinor = (long)(dto.UnitPrice * currencyFactor);

        // L-01: Guard against null ProductCode — Petronite may omit it for non-fuel items.
        if (string.IsNullOrWhiteSpace(dto.ProductCode))
        {
            throw new InvalidOperationException(
                $"Petronite ProductCode is null/empty for order {dto.OrderId}. " +
                "Cannot normalize transaction without a product code.");
        }

        // Product code mapping
        var productCode = _config.ProductCodeMapping.TryGetValue(dto.ProductCode, out var mapped)
            ? mapped
            : dto.ProductCode;

        // Timestamps — reject transactions with unparseable times to avoid placing them in wrong time windows
        if (!DateTimeOffset.TryParse(dto.StartTime, out var startedAt))
        {
            throw new InvalidOperationException(
                $"Petronite StartTime failed to parse for order {dto.OrderId} (raw: '{dto.StartTime}'). " +
                "Cannot normalize transaction without a valid start timestamp.");
        }

        if (!DateTimeOffset.TryParse(dto.EndTime, out var completedAt))
        {
            throw new InvalidOperationException(
                $"Petronite EndTime failed to parse for order {dto.OrderId} (raw: '{dto.EndTime}'). " +
                "Cannot normalize transaction without a valid end timestamp.");
        }

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

}
