using System.Net.Http.Headers;
using System.Text.Json;
using FccMiddleware.Adapter.Doms.Internal;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Adapter.Doms;

/// <summary>
/// Cloud-side DOMS FCC adapter implementing IFccAdapter.
/// Supports push payload validation/normalization and pull-mode transaction fetch
/// per the DOMS MVP REST protocol (§5.5 of adapter interface contracts).
///
/// One instance is created per Resolve call by FccAdapterFactory, configured with
/// the site-specific SiteFccConfig and an HttpClient pointed at the DOMS base URL.
/// </summary>
public sealed class DomsCloudAdapter : IFccAdapter
{
    private const string AdapterVer = "1.0.0";
    private const string ContentTypeJson = "application/json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly SiteFccConfig _config;

    /// <param name="httpClient">
    /// HttpClient pre-configured with BaseAddress = http(s)://{host}:{port}/api/v1
    /// and the X-API-Key header set from config.ApiKey.
    /// </param>
    /// <param name="config">Site-specific FCC configuration.</param>
    public DomsCloudAdapter(HttpClient httpClient, SiteFccConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    // -------------------------------------------------------------------------
    // IFccAdapter.NormalizeTransaction
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public CanonicalTransaction NormalizeTransaction(RawPayloadEnvelope rawPayload)
    {
        var dto = ParseSingleTransaction(rawPayload.Payload);
        return MapToCanonical(dto, rawPayload.SiteCode, rawPayload.Vendor);
    }

    // -------------------------------------------------------------------------
    // IFccAdapter.ValidatePayload
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public ValidationResult ValidatePayload(RawPayloadEnvelope rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload.Payload))
            return ValidationResult.Fail("NULL_PAYLOAD", "Payload is null or empty.");

        if (rawPayload.Vendor != FccVendor.DOMS)
            return ValidationResult.Fail(
                "VENDOR_MISMATCH",
                $"Expected vendor DOMS but received {rawPayload.Vendor}.");

        if (!string.Equals(rawPayload.ContentType, ContentTypeJson, StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Fail(
                "UNSUPPORTED_MESSAGE_TYPE",
                $"DOMS adapter requires content-type application/json, got '{rawPayload.ContentType}'.");

        DomsTransactionDto dto;
        try
        {
            dto = ParseSingleTransaction(rawPayload.Payload);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail("INVALID_JSON", $"JSON parse error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return ValidationResult.Fail("UNSUPPORTED_MESSAGE_TYPE", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(dto.TransactionId))
            return ValidationResult.Fail(
                "MISSING_REQUIRED_FIELD",
                "DOMS field 'transactionId' is required but was null or empty.");

        if (dto.VolumeMicrolitres <= 0)
            return ValidationResult.Fail(
                "MISSING_REQUIRED_FIELD",
                "DOMS field 'volumeMicrolitres' must be > 0.");

        if (dto.AmountMinorUnits <= 0)
            return ValidationResult.Fail(
                "MISSING_REQUIRED_FIELD",
                "DOMS field 'amountMinorUnits' must be > 0.");

        if (dto.UnitPriceMinorPerLitre <= 0)
            return ValidationResult.Fail(
                "MISSING_REQUIRED_FIELD",
                "DOMS field 'unitPriceMinorPerLitre' must be > 0.");

        if (dto.PumpNumber < 1)
            return ValidationResult.Fail(
                "MISSING_REQUIRED_FIELD",
                "DOMS field 'pumpNumber' must be >= 1.");

        if (dto.NozzleNumber < 1)
            return ValidationResult.Fail(
                "MISSING_REQUIRED_FIELD",
                "DOMS field 'nozzleNumber' must be >= 1.");

        if (string.IsNullOrWhiteSpace(dto.ProductCode))
            return ValidationResult.Fail(
                "MISSING_REQUIRED_FIELD",
                "DOMS field 'productCode' is required but was null or empty.");

        if (dto.EndTime < dto.StartTime)
            return ValidationResult.Fail(
                "MISSING_REQUIRED_FIELD",
                "DOMS field 'endTime' must be >= 'startTime'.");

        return ValidationResult.Ok();
    }

    // -------------------------------------------------------------------------
    // IFccAdapter.FetchTransactionsAsync
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<TransactionBatch> FetchTransactionsAsync(
        FetchCursor cursor,
        CancellationToken cancellationToken = default)
    {
        var url = BuildFetchUrl(cursor);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ContentTypeJson));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"DOMS fetch failed for site {_config.SiteCode}: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            bool recoverable = (int)response.StatusCode is 408 or 429 or >= 500;

            throw new InvalidOperationException(
                $"DOMS returned HTTP {(int)response.StatusCode} for site {_config.SiteCode}. " +
                $"Recoverable={recoverable}. Body: {body[..Math.Min(body.Length, 512)]}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var domsResponse = JsonSerializer.Deserialize<DomsListResponse>(json, JsonOpts)
            ?? throw new InvalidOperationException("DOMS returned an empty or null response body.");

        var canonical = domsResponse.Transactions
            .Select(dto => MapToCanonical(dto, _config.SiteCode, FccVendor.DOMS))
            .ToList();

        return new TransactionBatch
        {
            Transactions = canonical,
            NextCursorToken = domsResponse.NextCursor,
            HasMore = domsResponse.HasMore,
            SourceBatchId = domsResponse.SourceBatchId
        };
    }

    // -------------------------------------------------------------------------
    // IFccAdapter.GetAdapterMetadata
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public AdapterInfo GetAdapterMetadata() => new()
    {
        Vendor = FccVendor.DOMS,
        AdapterVersion = AdapterVer,
        SupportedIngestionMethods = [IngestionMethod.PUSH, IngestionMethod.PULL],
        SupportsPreAuth = false,   // Cloud DOMS = false; edge DOMS = true (Kotlin adapter)
        SupportsPumpStatus = false, // Edge-only capability
        Protocol = "REST"
    };

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Parses the raw payload string as a single DOMS transaction.
    /// DOMS may push either a bare transaction object or a wrapper object with a
    /// "transactions" array; we accept both shapes without a config flag (§5.5).
    /// Multi-item array shapes are rejected here — the caller must iterate.
    /// </summary>
    private static DomsTransactionDto ParseSingleTransaction(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        // Shape 1: { "transactions": [ {...} ] }  — single-item wrapper
        if (root.TryGetProperty("transactions", out var arrayProp))
        {
            if (arrayProp.GetArrayLength() != 1)
                throw new InvalidOperationException(
                    "UNSUPPORTED_MESSAGE_TYPE: NormalizeTransaction accepts exactly one transaction. " +
                    "Iterate multi-item arrays before calling this method.");

            var single = arrayProp[0].GetRawText();
            return JsonSerializer.Deserialize<DomsTransactionDto>(single, JsonOpts)!;
        }

        // Shape 2: bare transaction object { "transactionId": "...", ... }
        return JsonSerializer.Deserialize<DomsTransactionDto>(payload, JsonOpts)!;
    }

    private CanonicalTransaction MapToCanonical(
        DomsTransactionDto dto,
        string siteCode,
        FccVendor vendor)
    {
        var canonicalProductCode = _config.ProductCodeMapping.TryGetValue(dto.ProductCode, out var mapped)
            ? mapped
            : dto.ProductCode;

        var canonicalPumpNumber = dto.PumpNumber - _config.PumpNumberOffset;

        return new CanonicalTransaction
        {
            FccTransactionId = dto.TransactionId,
            SiteCode = siteCode,
            PumpNumber = canonicalPumpNumber,
            NozzleNumber = dto.NozzleNumber,
            ProductCode = canonicalProductCode,
            VolumeMicrolitres = dto.VolumeMicrolitres,
            AmountMinorUnits = dto.AmountMinorUnits,
            UnitPriceMinorPerLitre = dto.UnitPriceMinorPerLitre,
            CurrencyCode = _config.CurrencyCode,
            StartedAt = dto.StartTime,
            CompletedAt = dto.EndTime,
            FccCorrelationId = dto.FccCorrelationId,
            OdooOrderId = dto.OdooOrderId,
            FccVendor = vendor,
            FiscalReceiptNumber = dto.ReceiptNumber,
            AttendantId = dto.AttendantId
        };
    }

    private string BuildFetchUrl(FetchCursor cursor)
    {
        var query = new List<string>();

        if (!string.IsNullOrEmpty(cursor.CursorToken))
            query.Add($"cursor={Uri.EscapeDataString(cursor.CursorToken)}");
        else if (cursor.SinceUtc.HasValue)
            query.Add($"since={Uri.EscapeDataString(cursor.SinceUtc.Value.ToString("O"))}");

        if (cursor.Limit.HasValue)
            query.Add($"limit={cursor.Limit.Value}");

        var qs = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;
        return $"transactions{qs}";
    }
}
