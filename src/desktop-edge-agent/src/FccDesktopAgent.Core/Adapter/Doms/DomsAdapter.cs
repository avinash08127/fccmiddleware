using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Doms;

/// <summary>
/// DOMS-protocol FCC adapter. Communicates with the Forecourt Controller
/// over the station LAN using the DOMS HTTP REST API.
/// Base URL and API key come from <see cref="FccConnectionConfig"/>.
/// Uses the named HTTP client "fcc" from <see cref="IHttpClientFactory"/>.
/// </summary>
public sealed class DomsAdapter : IFccAdapter
{
    /// <inheritdoc />
    public PumpStatusCapability PumpStatusCapability => PumpStatusCapability.Live;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpFactory;
    private readonly FccConnectionConfig _config;
    private readonly ILogger<DomsAdapter> _logger;

    public DomsAdapter(IHttpClientFactory httpFactory, FccConnectionConfig config, ILogger<DomsAdapter> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<CanonicalTransaction> NormalizeAsync(RawPayloadEnvelope rawPayload, CancellationToken ct)
    {
        var tx = JsonSerializer.Deserialize<DomsTransaction>(rawPayload.RawJson, JsonOptions)
            ?? throw new FccAdapterException("DOMS normalization: null result from deserialization", isRecoverable: false);

        // DOMS reports volume in litres (decimal). Convert to microlitres using decimal arithmetic to avoid float precision loss.
        var volumeMicrolitres = (long)(tx.VolumeLitres * 1_000_000m);

        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CanonicalTransaction
        {
            FccTransactionId = tx.TransactionId,
            SiteCode = rawPayload.SiteCode,
            LegalEntityId = rawPayload.SiteCode,
            PumpNumber = tx.PumpNumber,
            NozzleNumber = tx.NozzleNumber,
            ProductCode = tx.ProductCode,
            VolumeMicrolitres = volumeMicrolitres,
            AmountMinorUnits = tx.AmountMinorUnits,
            UnitPriceMinorPerLitre = tx.UnitPriceMinorPerLitre,
            CurrencyCode = tx.CurrencyCode,
            StartedAt = tx.StartedAt,
            CompletedAt = tx.CompletedAt,
            FiscalReceiptNumber = tx.FiscalReceiptNumber,
            FccVendor = "DOMS",
            AttendantId = tx.AttendantId,
            IngestionSource = "EdgeUpload",
            IngestedAt = now,
            UpdatedAt = now,
            RawPayloadJson = rawPayload.RawJson,
        });
    }

    /// <inheritdoc/>
    public async Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct)
    {
        var requestBody = new DomsPreAuthRequest(
            PreAuthId: command.PreAuthId,
            PumpNumber: command.FccPumpNumber,
            NozzleNumber: command.FccNozzleNumber,
            ProductCode: command.ProductCode,
            AmountMinorUnits: command.RequestedAmountMinorUnits,
            UnitPriceMinorPerLitre: command.UnitPriceMinorPerLitre,
            CurrencyCode: command.Currency,
            VehicleNumber: command.VehicleNumber,
            CorrelationId: command.FccCorrelationId);

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Post, "/api/v1/preauth", content, ct);
        }
        catch (FccAdapterException ex)
        {
            _logger.LogWarning(ex, "DOMS pre-auth HTTP error (pump {Pump})", command.FccPumpNumber);
            return new PreAuthResult(false, null, null, $"HTTP_{ex.HttpStatusCode}", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DOMS pre-auth transport error (pump {Pump})", command.FccPumpNumber);
            return new PreAuthResult(false, null, null, "TRANSPORT_ERROR", ex.Message);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<DomsPreAuthResponse>(body, JsonOptions);

            if (result is null)
                return new PreAuthResult(false, null, null, "PARSE_ERROR", "Empty or unparseable DOMS pre-auth response");

            return new PreAuthResult(
                Accepted: result.Accepted,
                FccCorrelationId: result.CorrelationId,
                FccAuthorizationCode: result.AuthorizationCode,
                ErrorCode: result.ErrorCode,
                ErrorMessage: result.Message,
                ExpiresAt: result.ExpiresAtUtc);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Get, "/api/v1/pump-status", null, ct);
        }
        catch (FccAdapterException ex)
        {
            _logger.LogWarning(ex, "DOMS pump-status HTTP error");
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DOMS pump-status transport error");
            return [];
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var items = JsonSerializer.Deserialize<List<DomsPumpStatusItem>>(body, JsonOptions) ?? [];
            return items.Select(MapPumpStatus).ToList();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Uses a 5-second hard deadline regardless of the named client's default timeout.</remarks>
    public async Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var http = _httpFactory.CreateClient("fcc");
            using var request = BuildRequest(HttpMethod.Get, "/api/v1/heartbeat");
            using var response = await http.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
                return false;

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var heartbeat = JsonSerializer.Deserialize<DomsHeartbeatResponse>(body, JsonOptions);
            return heartbeat?.Status?.Equals("UP", StringComparison.OrdinalIgnoreCase) ?? false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Inner 5-second timeout fired — FCC is unresponsive
            _logger.LogDebug("DOMS heartbeat timed out");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "DOMS heartbeat failed");
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct)
    {
        var url = BuildFetchUrl(cursor);

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Get, url, null, ct);
        }
        catch (FccAdapterException ex)
        {
            _logger.LogWarning(ex, "DOMS fetch-transactions HTTP error");
            return new TransactionBatch([], null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DOMS fetch-transactions transport error");
            return new TransactionBatch([], null, false);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var domsResponse = JsonSerializer.Deserialize<DomsTransactionListResponse>(body, JsonOptions);

            if (domsResponse is null)
                return new TransactionBatch([], null, false);

            var now = DateTimeOffset.UtcNow;
            var records = domsResponse.Transactions
                .Select(tx => new RawPayloadEnvelope(
                    FccVendor: "DOMS",
                    SiteCode: _config.SiteCode,
                    RawJson: JsonSerializer.Serialize(tx, JsonOptions),
                    ReceivedAt: now))
                .ToList();

            return new TransactionBatch(records, domsResponse.NextCursor, domsResponse.HasMore);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CancelPreAuthAsync(string fccCorrelationId, CancellationToken ct)
    {
        try
        {
            var response = await SendAsync(
                HttpMethod.Delete,
                $"/api/v1/preauth/{Uri.EscapeDataString(fccCorrelationId)}",
                null, ct);
            using (response) { return true; }
        }
        catch (FccAdapterException ex) when (ex.HttpStatusCode == 404)
        {
            // Already gone at FCC — treat as success (idempotent)
            return false;
        }
        catch (FccAdapterException ex)
        {
            _logger.LogWarning(ex, "DOMS cancel pre-auth HTTP error for {CorrelationId}", fccCorrelationId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DOMS cancel pre-auth transport error for {CorrelationId}", fccCorrelationId);
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>No-op — DOMS uses cursor-based acknowledgment implicit in FetchTransactionsAsync.</remarks>
    public Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct)
        => Task.FromResult(true);

    // ── Private helpers ──────────────────────────────────────────────────────

    private bool _fccHttpWarningLogged;

    private HttpRequestMessage BuildRequest(HttpMethod method, string path, HttpContent? content = null)
    {
        var baseUri = new Uri(_config.BaseUrl.TrimEnd('/') + "/");

        // S-DSK-023: Warn when FCC API key is sent over non-HTTPS connection.
        if (!_fccHttpWarningLogged
            && !string.Equals(baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "FCC base URL uses {Scheme} — X-API-Key header will be sent without TLS encryption. " +
                "Use HTTPS for production FCC connections to prevent credential exposure on the LAN",
                baseUri.Scheme);
            _fccHttpWarningLogged = true;
        }

        var requestUri = new Uri(baseUri, path.TrimStart('/'));
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-API-Key", _config.ApiKey);
        if (content is not null)
            request.Content = content;
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("fcc");
        using var request = BuildRequest(method, path, content);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FccAdapterException($"DOMS {path} transport failure", isRecoverable: true, ex);
        }

        ThrowIfHttpError(response, path);
        return response;
    }

    private static void ThrowIfHttpError(HttpResponseMessage response, string path)
    {
        if (response.IsSuccessStatusCode) return;

        var statusCode = (int)response.StatusCode;
        // 401/403 = auth misconfiguration — non-recoverable
        // 408/429/5xx = transient — recoverable
        var isRecoverable = statusCode is 408 or 429 || statusCode >= 500;

        throw new FccAdapterException(
            $"DOMS {path} returned HTTP {statusCode}",
            isRecoverable,
            httpStatusCode: statusCode);
    }

    private static string BuildFetchUrl(FetchCursor cursor)
    {
        var sb = new StringBuilder("/api/v1/transactions?limit=").Append(cursor.MaxCount);
        if (cursor.Since.HasValue)
            sb.Append("&since=").Append(Uri.EscapeDataString(cursor.Since.Value.UtcDateTime.ToString("O")));
        if (!string.IsNullOrEmpty(cursor.LastSequence))
            sb.Append("&cursor=").Append(Uri.EscapeDataString(cursor.LastSequence));
        return sb.ToString();
    }

    private PumpStatus MapPumpStatus(DomsPumpStatusItem item) => new()
    {
        SiteCode = _config.SiteCode,
        PumpNumber = item.PumpNumber,
        NozzleNumber = item.NozzleNumber,
        State = ParsePumpState(item.State),
        ProductCode = item.ProductCode,
        ProductName = item.ProductName,
        CurrencyCode = item.CurrencyCode,
        CurrentVolumeLitres = item.CurrentVolumeLitres,
        CurrentAmount = item.CurrentAmount,
        UnitPrice = item.UnitPrice,
        StatusSequence = item.StatusSequence,
        FccStatusCode = item.FccStatusCode,
        LastChangedAtUtc = item.LastChangedAt,
        ObservedAtUtc = DateTimeOffset.UtcNow,
        Source = PumpStatusSource.FccLive,
    };

    private static PumpState ParsePumpState(string state) => state.ToUpperInvariant() switch
    {
        "IDLE"       => PumpState.Idle,
        "AUTHORIZED" => PumpState.Authorized,
        "CALLING"    => PumpState.Calling,
        "DISPENSING" => PumpState.Dispensing,
        "PAUSED"     => PumpState.Paused,
        "COMPLETED"  => PumpState.Completed,
        "ERROR"      => PumpState.Error,
        "OFFLINE"    => PumpState.Offline,
        _            => PumpState.Unknown,
    };
}
