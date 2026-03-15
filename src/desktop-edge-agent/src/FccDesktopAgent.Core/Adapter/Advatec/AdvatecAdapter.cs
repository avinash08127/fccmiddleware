using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Advatec;

/// <summary>
/// Desktop .NET adapter for Advatec TRA-compliant Electronic Fiscal Devices.
///
/// Advatec is a fiscal device running on localhost:5560 that also triggers pump
/// authorization via Customer data submission (Scenario C). This adapter handles:
///   1. Customer data submission → pump authorization (pre-auth)
///   2. Receipt webhook JSON normalization to CanonicalTransaction
///   3. Pre-auth ↔ Receipt correlation via customer data matching
///   4. Heartbeat via TCP connect to the local Advatec device
///   5. Webhook listener lifecycle (lazy-started on first FetchTransactionsAsync call)
///
/// Push-only: transactions arrive via <see cref="AdvatecWebhookListener"/> and are
/// queued internally. <see cref="FetchTransactionsAsync"/> drains that queue so the
/// standard ingestion pipeline buffers them.
/// </summary>
public sealed class AdvatecAdapter : IFccAdapter, IAsyncDisposable
{
    /// <inheritdoc />
    public PumpStatusCapability PumpStatusCapability => PumpStatusCapability.NotApplicable;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const int DefaultHeartbeatTimeoutMs = 5000;
    private const int DefaultWebhookListenerPort = 8091;
    private const int DefaultDevicePort = 5560;
    private const int DefaultApiRequestTimeoutSeconds = 10;

    /// <summary>Maximum age for an active pre-auth entry before it's considered stale.</summary>
    private static readonly TimeSpan PreAuthTtl = TimeSpan.FromMinutes(30);

    private readonly FccConnectionConfig _config;
    private readonly ILogger<AdvatecAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory? _httpClientFactory;

    // ── Webhook listener (ADV-3.1) ───────────────────────────────────────────

    private readonly ConcurrentQueue<RawPayloadEnvelope> _webhookQueue = new();
    private AdvatecWebhookListener? _webhookListener;
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // ── Pre-auth tracking (ADV-4.2, ADV-4.4) ────────────────────────────────

    /// <summary>
    /// Active pre-authorizations keyed by pump number. One active pre-auth per pump.
    /// Populated by <see cref="SendPreAuthAsync"/>, consumed by receipt correlation
    /// in <see cref="MapToCanonical"/>.
    /// </summary>
    private readonly ConcurrentDictionary<int, ActivePreAuth> _activePreAuths = new();

    /// <summary>Serializes purge + match operations to prevent TOCTOU races on _activePreAuths.</summary>
    private readonly object _preAuthCorrelationLock = new();

    /// <summary>Lazily created HTTP client for Advatec Customer data submission.</summary>
    private AdvatecApiClient? _apiClient;
    private readonly object _apiClientLock = new();

    public AdvatecAdapter(
        FccConnectionConfig config,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<AdvatecAdapter>();
    }

    /// <summary>
    /// Backward-compatible constructor for cases where only ILogger is available.
    /// </summary>
    public AdvatecAdapter(FccConnectionConfig config, ILogger<AdvatecAdapter> logger)
    {
        _config = config;
        _logger = logger;
        _loggerFactory = null!;
        _httpClientFactory = null;
    }

    // ── NormalizeAsync ────────────────────────────────────────────────────────

    public Task<CanonicalTransaction> NormalizeAsync(
        RawPayloadEnvelope rawPayload, CancellationToken ct)
    {
        AdvatecWebhookEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AdvatecWebhookEnvelope>(rawPayload.RawJson, JsonOpts)
                ?? throw new InvalidOperationException("Deserialized Advatec payload was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse Advatec webhook JSON: {ex.Message}", ex);
        }

        if (!string.Equals(envelope.DataType, "Receipt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Advatec DataType '{envelope.DataType}' is not 'Receipt'.");

        if (envelope.Data is null)
            throw new InvalidOperationException("Advatec webhook payload has no Data.");

        // M-11: Purge + match under lock to prevent TOCTOU race where purge removes
        // an entry between match's read and TryRemove.
        ActivePreAuth? matchedPreAuth;
        lock (_preAuthCorrelationLock)
        {
            PurgeStalePreAuths();
            matchedPreAuth = TryMatchPreAuth(envelope.Data);
        }

        var canonical = MapToCanonical(envelope.Data, rawPayload, matchedPreAuth);
        return Task.FromResult(canonical);
    }

    // ── FetchTransactionsAsync (drain webhook queue) ─────────────────────────

    public async Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        if (_webhookQueue.IsEmpty)
            return new TransactionBatch([], null, false);

        var records = new List<RawPayloadEnvelope>(Math.Min(cursor.MaxCount, _webhookQueue.Count));
        while (records.Count < cursor.MaxCount && _webhookQueue.TryDequeue(out var envelope))
        {
            records.Add(envelope);
        }

        var hasMore = !_webhookQueue.IsEmpty;
        return new TransactionBatch(records, null, hasMore);
    }

    // ── Lazy initialization (webhook listener) ───────────────────────────────

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var port = _config.AdvatecWebhookListenerPort ?? DefaultWebhookListenerPort;
            try
            {
                if (_loggerFactory is not null)
                {
                    _webhookListener = new AdvatecWebhookListener(
                        port,
                        _config.SiteCode,
                        _config.AdvatecWebhookToken,
                        _loggerFactory.CreateLogger<AdvatecWebhookListener>());
                }
                else
                {
                    _webhookListener = new AdvatecWebhookListener(
                        port,
                        _config.SiteCode,
                        _config.AdvatecWebhookToken,
                        Microsoft.Extensions.Logging.Abstractions.NullLogger<AdvatecWebhookListener>.Instance);
                }

                _webhookListener.Start(OnWebhookReceivedAsync, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Advatec webhook listener failed to start on port {Port}; "
                    + "push transactions will not be received until the port is available", port);
                // Do NOT set _initialized so the next call retries listener startup.
                return;
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private Task OnWebhookReceivedAsync(RawPayloadEnvelope payload, CancellationToken ct)
    {
        _webhookQueue.Enqueue(payload);
        _logger.LogDebug(
            "Advatec webhook payload enqueued (queue size: {Size})", _webhookQueue.Count);
        return Task.CompletedTask;
    }

    // ── SendPreAuthAsync (ADV-4.2) ───────────────────────────────────────────

    /// <summary>
    /// Submits Customer data to Advatec to trigger pump authorization.
    /// Maps <see cref="PreAuthCommand"/> fields to <see cref="AdvatecCustomerRequest"/>,
    /// stores correlation entry for receipt matching.
    /// </summary>
    public async Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct)
    {
        var client = GetOrCreateApiClient();

        // Convert amount from minor units to major currency units for Dose (volume in litres)
        // Dose = requested amount / unit price (both in minor units) → litres
        var doseLitres = command.UnitPriceMinorPerLitre > 0
            ? (decimal)command.RequestedAmountMinorUnits / command.UnitPriceMinorPerLitre
            : 0m;

        var custIdType = command.CustomerIdType
            ?? _config.AdvatecCustIdType
            ?? 6; // 6 = NIL (TRA default)

        var request = new AdvatecCustomerRequest
        {
            DataType = "Customer",
            Data = new AdvatecCustomerData
            {
                Pump = command.FccPumpNumber,
                Dose = Math.Round(doseLitres, 4),
                CustIdType = custIdType,
                CustomerId = command.CustomerTaxId ?? "",
                CustomerName = command.CustomerName ?? "",
                Payments = [], // Empty during pre-auth per AQ-5
            },
        };

        var result = await client.SubmitCustomerDataAsync(request, ct);

        if (!result.Success)
        {
            var errorCode = result.StatusCode == 0 ? "TIMEOUT" : $"HTTP_{result.StatusCode}";
            return new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: errorCode,
                ErrorMessage: result.ErrorMessage ?? $"Advatec returned HTTP {result.StatusCode}: {result.ResponseBody}");
        }

        // Store active pre-auth for receipt correlation (ADV-4.4)
        var correlationId = $"ADV-{command.FccPumpNumber}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var activePreAuth = new ActivePreAuth(
            PumpNumber: command.FccPumpNumber,
            CorrelationId: correlationId,
            OdooOrderId: command.FccCorrelationId,
            PreAuthId: command.PreAuthId,
            CustomerId: command.CustomerTaxId,
            CustomerName: command.CustomerName,
            DoseLitres: doseLitres,
            CreatedAt: DateTimeOffset.UtcNow);

        // One pre-auth per pump — overwrite any stale entry
        _activePreAuths[command.FccPumpNumber] = activePreAuth;

        _logger.LogInformation(
            "Advatec pre-auth stored: Pump={Pump}, CorrelationId={CorrelationId}, " +
            "Dose={Dose}L, OdooOrderId={OdooOrderId}",
            command.FccPumpNumber, correlationId, doseLitres, command.FccCorrelationId);

        return new PreAuthResult(
            Accepted: true,
            FccCorrelationId: correlationId,
            FccAuthorizationCode: null, // Advatec doesn't return an auth code
            ErrorCode: null,
            ErrorMessage: null);
    }

    // ── CancelPreAuthAsync (ADV-4.2) ─────────────────────────────────────────

    /// <summary>
    /// Removes the active pre-auth entry. Advatec has no documented cancel API,
    /// but we clean up the correlation map so the receipt won't be matched.
    /// </summary>
    public Task<bool> CancelPreAuthAsync(string fccCorrelationId, CancellationToken ct)
    {
        // Validate correlation ID format: must match "ADV-{pump}-{timestamp}"
        if (string.IsNullOrWhiteSpace(fccCorrelationId) || !IsValidCorrelationId(fccCorrelationId))
        {
            _logger.LogWarning(
                "Advatec cancel pre-auth: invalid correlation ID format '{CorrelationId}'. " +
                "Expected 'ADV-{{pump}}-{{timestamp}}'", fccCorrelationId);
            return Task.FromResult(false);
        }

        // Find and remove by correlationId
        foreach (var kvp in _activePreAuths)
        {
            if (string.Equals(kvp.Value.CorrelationId, fccCorrelationId, StringComparison.Ordinal))
            {
                var removed = _activePreAuths.TryRemove(kvp.Key, out _);
                if (removed)
                {
                    _logger.LogInformation(
                        "Advatec pre-auth cancelled: CorrelationId={CorrelationId}, Pump={Pump}",
                        fccCorrelationId, kvp.Key);
                }
                return Task.FromResult(removed);
            }
        }

        _logger.LogWarning(
            "Advatec cancel pre-auth: no active pre-auth found for valid CorrelationId={CorrelationId}. " +
            "It may have already been matched to a receipt or expired (TTL={TtlMinutes}min)",
            fccCorrelationId, PreAuthTtl.TotalMinutes);
        return Task.FromResult(false);
    }

    /// <summary>
    /// Validates that a correlation ID matches the expected format: ADV-{pumpNumber}-{unixMs}.
    /// </summary>
    private static bool IsValidCorrelationId(string correlationId)
    {
        // Format: "ADV-{int}-{long}"
        if (!correlationId.StartsWith("ADV-", StringComparison.Ordinal))
            return false;

        var remainder = correlationId.AsSpan(4); // after "ADV-"
        var dashIdx = remainder.IndexOf('-');
        if (dashIdx <= 0 || dashIdx == remainder.Length - 1)
            return false;

        return int.TryParse(remainder[..dashIdx], out _)
            && long.TryParse(remainder[(dashIdx + 1)..], out _);
    }

    // ── GetPumpStatusAsync ───────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Advatec EFDs are receipt-only fiscal devices — they have no concept of pumps or
    /// real-time dispenser state. PumpStatusCapability is NotApplicable for this adapter.
    /// </remarks>
    public Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<PumpStatus>>(Array.Empty<PumpStatus>());
    }

    // ── HeartbeatAsync ───────────────────────────────────────────────────────

    public async Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        var host = _config.AdvatecDeviceAddress ?? "127.0.0.1";
        var port = _config.AdvatecDevicePort ?? DefaultDevicePort;
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeatMs = (_config.HeartbeatIntervalSeconds ?? 5) * 1000;
            timeoutCts.CancelAfter(heartbeatMs > 0 ? heartbeatMs : DefaultHeartbeatTimeoutMs);
            await client.ConnectAsync(host, port, timeoutCts.Token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Heartbeat failed for Advatec at {Host}:{Port} — {Message}",
                host, port, ex.Message);
            return false;
        }
    }

    // ── AcknowledgeTransactionsAsync ─────────────────────────────────────────

    public Task<bool> AcknowledgeTransactionsAsync(
        IReadOnlyList<string> transactionIds, CancellationToken ct)
    {
        return Task.FromResult(true);
    }

    // ── Adapter metadata ─────────────────────────────────────────────────────

    public IReadOnlyDictionary<string, string> GetAdapterMetadata() => new Dictionary<string, string>
    {
        ["vendor"] = "Advatec",
        ["protocol"] = "REST_JSON",
        ["ingestionModel"] = "PUSH-only",
        ["webhookListening"] = (_webhookListener?.IsListening ?? false).ToString(),
        ["webhookQueueSize"] = _webhookQueue.Count.ToString(),
        ["supportsPreAuth"] = "true",
        ["supportsPumpStatus"] = "false",
        ["countryScope"] = "TZ",
        ["activePreAuths"] = _activePreAuths.Count.ToString(),
    };

    public int WebhookQueueSize => _webhookQueue.Count;
    public bool IsWebhookListening => _webhookListener?.IsListening ?? false;
    public int ActivePreAuthCount => _activePreAuths.Count;

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_webhookListener is not null)
        {
            await _webhookListener.StopAsync();
            _webhookListener.Dispose();
            _webhookListener = null;
        }

        _apiClient = null;

        _initLock.Dispose();
    }

    // ── Private: API client ─────────────────────────────────────────────────

    private AdvatecApiClient GetOrCreateApiClient()
    {
        if (_apiClient is not null) return _apiClient;

        lock (_apiClientLock)
        {
            if (_apiClient is not null) return _apiClient;

            var host = _config.AdvatecDeviceAddress ?? "127.0.0.1";
            var port = _config.AdvatecDevicePort ?? DefaultDevicePort;

            // H-05: Use IHttpClientFactory to avoid socket exhaustion and memory leaks.
            // The factory manages HttpClient/HttpMessageHandler lifetimes properly.
            var httpClient = _httpClientFactory?.CreateClient("Advatec") ?? new HttpClient();
            var apiTimeout = _config.ApiRequestTimeoutSeconds ?? DefaultApiRequestTimeoutSeconds;
            httpClient.Timeout = TimeSpan.FromSeconds(apiTimeout);

            _apiClient = new AdvatecApiClient(httpClient, host, port, _logger);
            return _apiClient;
        }
    }

    // ── Private: receipt ↔ pre-auth correlation (ADV-4.4) ───────────────────

    /// <summary>
    /// Attempts to find a matching active pre-auth for an incoming receipt.
    /// Strategy:
    ///   1. Match by CustomerId if both the receipt and a pre-auth have one.
    ///   2. Fallback: match the oldest active pre-auth within the TTL window (FIFO).
    /// Returns null if no match found (Normal Order).
    /// </summary>
    private ActivePreAuth? TryMatchPreAuth(AdvatecReceiptData receipt)
    {
        if (_activePreAuths.IsEmpty)
            return null;

        // Strategy 1: Match by CustomerId (receipt echoes back the customer data we submitted)
        if (!string.IsNullOrWhiteSpace(receipt.CustomerId))
        {
            foreach (var kvp in _activePreAuths)
            {
                if (string.Equals(kvp.Value.CustomerId, receipt.CustomerId, StringComparison.OrdinalIgnoreCase)
                    && (DateTimeOffset.UtcNow - kvp.Value.CreatedAt) < PreAuthTtl)
                {
                    _activePreAuths.TryRemove(kvp.Key, out _);
                    _logger.LogInformation(
                        "Advatec receipt correlated by CustomerId: Pump={Pump}, CorrelationId={CorrelationId}, " +
                        "OdooOrderId={OdooOrderId}",
                        kvp.Value.PumpNumber, kvp.Value.CorrelationId, kvp.Value.OdooOrderId);
                    return kvp.Value;
                }
            }
        }

        // Strategy 2: FIFO — oldest active pre-auth within TTL
        // Advatec processes requests sequentially on a single device, so the oldest
        // pending pre-auth is the most likely match for the next receipt.
        ActivePreAuth? oldest = null;
        int oldestKey = -1;
        foreach (var kvp in _activePreAuths)
        {
            if ((DateTimeOffset.UtcNow - kvp.Value.CreatedAt) >= PreAuthTtl)
                continue; // skip stale

            if (oldest is null || kvp.Value.CreatedAt < oldest.CreatedAt)
            {
                oldest = kvp.Value;
                oldestKey = kvp.Key;
            }
        }

        if (oldest is not null && oldestKey >= 0)
        {
            _activePreAuths.TryRemove(oldestKey, out _);
            _logger.LogInformation(
                "Advatec receipt correlated by FIFO: Pump={Pump}, CorrelationId={CorrelationId}, " +
                "OdooOrderId={OdooOrderId}",
                oldest.PumpNumber, oldest.CorrelationId, oldest.OdooOrderId);
        }

        return oldest;
    }

    /// <summary>
    /// Removes pre-auth entries older than <see cref="PreAuthTtl"/>.
    /// Called during normalization to prevent memory leaks.
    /// </summary>
    private void PurgeStalePreAuths()
    {
        foreach (var kvp in _activePreAuths)
        {
            if ((DateTimeOffset.UtcNow - kvp.Value.CreatedAt) >= PreAuthTtl)
            {
                if (_activePreAuths.TryRemove(kvp.Key, out var removed))
                {
                    _logger.LogWarning(
                        "Advatec stale pre-auth purged: Pump={Pump}, CorrelationId={CorrelationId}, " +
                        "Age={Age}min",
                        removed.PumpNumber, removed.CorrelationId,
                        (DateTimeOffset.UtcNow - removed.CreatedAt).TotalMinutes);
                }
            }
        }
    }

    // ── Private: mapping ─────────────────────────────────────────────────────

    private CanonicalTransaction MapToCanonical(
        AdvatecReceiptData receipt, RawPayloadEnvelope rawPayload, ActivePreAuth? matchedPreAuth = null)
    {
        if (string.IsNullOrWhiteSpace(receipt.TransactionId))
            throw new InvalidOperationException("Advatec Receipt missing TransactionId.");

        if (receipt.Items is null || receipt.Items.Count == 0)
            throw new InvalidOperationException("Advatec Receipt has no Items.");

        var item = receipt.Items[0];

        // Volume: Quantity is in litres -> microlitres (decimal, no float)
        var volumeMicrolitres = (long)(item.Quantity * 1_000_000m);

        // Amount & price conversion via currency factor
        var currencyFactor = CurrencyHelper.GetCurrencyFactor(_config.CurrencyCode ?? "TZS");
        var amountMinorUnits = (long)(receipt.AmountInclusive * currencyFactor);
        var unitPriceMinorPerLitre = (long)(item.Price * currencyFactor);

        // Sanity check: price * quantity ~= amount (within discount tolerance)
        var expectedAmount = item.Price * item.Quantity;
        var diff = Math.Abs(expectedAmount - item.Amount - (item.DiscountAmount ?? 0m));
        if (diff > 1m)
        {
            _logger.LogWarning(
                "Sanity check: Price({Price}) * Qty({Qty}) = {Expected} but Item.Amount={Actual}, " +
                "Discount={Discount}, diff={Diff}",
                item.Price, item.Quantity, expectedAmount, item.Amount,
                item.DiscountAmount ?? 0m, diff);
        }

        // Product code mapping
        var rawProduct = item.Product ?? "UNKNOWN";
        var productCode = _config.ProductCodeMapping?.TryGetValue(rawProduct, out var mapped) == true
            ? mapped
            : rawProduct;

        // Timestamps: Date + Time with configured timezone -> UTC
        var tz = TimeZoneInfo.FindSystemTimeZoneById(_config.Timezone ?? "E. Africa Standard Time");
        var completedAt = ParseAdvatecTimestamp(receipt.Date, receipt.Time, tz)
            ?? rawPayload.ReceivedAt;
        var startedAt = completedAt; // Only one timestamp available

        // Dedup key: {siteCode}-{TransactionId}
        var fccTransactionId = $"{_config.SiteCode}-{receipt.TransactionId}";

        // Pump number: from pre-auth if matched, otherwise config default (AQ-3)
        var pumpNumber = matchedPreAuth?.PumpNumber ?? (0 + _config.PumpNumberOffset);

        var correlationId = matchedPreAuth?.CorrelationId;
        var odooOrderId = matchedPreAuth?.OdooOrderId;
        var preAuthId = matchedPreAuth?.PreAuthId;

        // S-DSK-021: Do not log CustomerId (may contain customer tax ID — regulated PII).
        if (matchedPreAuth is null && !string.IsNullOrWhiteSpace(receipt.CustomerId))
        {
            _logger.LogDebug(
                "Advatec receipt has CustomerId present but no matching pre-auth — Normal Order on Pump={Pump}",
                pumpNumber);
        }

        var now = DateTimeOffset.UtcNow;

        return new CanonicalTransaction
        {
            Id = Guid.NewGuid().ToString(),
            FccTransactionId = fccTransactionId,
            SiteCode = _config.SiteCode,
            PumpNumber = pumpNumber,
            NozzleNumber = 1, // AQ-9: no nozzle concept
            ProductCode = productCode,
            VolumeMicrolitres = volumeMicrolitres,
            AmountMinorUnits = amountMinorUnits,
            UnitPriceMinorPerLitre = unitPriceMinorPerLitre,
            CurrencyCode = _config.CurrencyCode ?? "TZS",
            StartedAt = startedAt,
            CompletedAt = completedAt,
            FiscalReceiptNumber = receipt.ReceiptCode,
            FccVendor = nameof(FccVendor.Advatec),
            LegalEntityId = _config.LegalEntityId ?? _config.SiteCode,
            Status = TransactionStatus.Pending,
            IngestionSource = "FCC_PUSH",
            IngestedAt = now,
            UpdatedAt = now,
            RawPayloadJson = rawPayload.RawJson,
            CorrelationId = correlationId,
            OdooOrderId = odooOrderId,
            PreAuthId = preAuthId,
        };
    }

    private static DateTimeOffset? ParseAdvatecTimestamp(
        string? date, string? time, TimeZoneInfo tz)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        try
        {
            var localDate = DateOnly.ParseExact(date, "yyyy-MM-dd");
            var localTime = !string.IsNullOrWhiteSpace(time)
                ? TimeOnly.ParseExact(time, "HH:mm:ss")
                : TimeOnly.MinValue;

            var dateTime = new DateTime(localDate, localTime, DateTimeKind.Unspecified);
            var offset = tz.GetUtcOffset(dateTime);
            return new DateTimeOffset(dateTime, offset).ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

}

/// <summary>
/// Tracks an in-flight pre-authorization awaiting its Receipt webhook.
/// Keyed by pump number in <see cref="AdvatecAdapter._activePreAuths"/>.
/// </summary>
public sealed record ActivePreAuth(
    int PumpNumber,
    string CorrelationId,
    string? OdooOrderId,
    string? PreAuthId,
    string? CustomerId,
    string? CustomerName,
    decimal DoseLitres,
    DateTimeOffset CreatedAt);
