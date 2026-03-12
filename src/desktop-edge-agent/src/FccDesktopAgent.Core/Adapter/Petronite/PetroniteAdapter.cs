using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Petronite;

/// <summary>
/// Petronite-protocol FCC adapter. Communicates with the Petronite cloud API
/// via OAuth2-authenticated REST calls.
/// <para>
/// Push-only model: transactions arrive via webhooks, so <see cref="FetchTransactionsAsync"/>
/// always returns an empty batch. <see cref="HeartbeatAsync"/> uses GET /nozzles/assigned
/// as the liveness probe. Pre-auth is two-step: create order + authorize pump.
/// </para>
/// </summary>
public sealed class PetroniteAdapter : IFccAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpFactory;
    private readonly FccConnectionConfig _config;
    private readonly PetroniteOAuthClient _oauthClient;
    private readonly PetroniteNozzleResolver _nozzleResolver;
    private readonly ILogger<PetroniteAdapter> _logger;

    public PetroniteAdapter(
        IHttpClientFactory httpFactory,
        FccConnectionConfig config,
        PetroniteOAuthClient oauthClient,
        PetroniteNozzleResolver nozzleResolver,
        ILogger<PetroniteAdapter> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _oauthClient = oauthClient;
        _nozzleResolver = nozzleResolver;
        _logger = logger;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Parses a Petronite webhook JSON payload into a canonical transaction.
    /// Volume is converted from litres (decimal) to microlitres (long).
    /// Amount is converted from major currency units (decimal) to minor units (long).
    /// </remarks>
    public Task<CanonicalTransaction> NormalizeAsync(RawPayloadEnvelope rawPayload, CancellationToken ct)
    {
        var webhook = JsonSerializer.Deserialize<PetroniteWebhookPayload>(rawPayload.RawJson, JsonOptions)
            ?? throw new FccAdapterException(
                "Petronite normalization: null result from deserialization",
                isRecoverable: false);

        var tx = webhook.Transaction
            ?? throw new FccAdapterException(
                "Petronite normalization: webhook payload has no transaction data",
                isRecoverable: false);

        // Convert volume from litres (decimal) to microlitres (long) using decimal arithmetic.
        var volumeMicrolitres = (long)(tx.VolumeLitres * 1_000_000m);

        // Convert amount from major units (e.g. dollars) to minor units (e.g. cents).
        var amountMinorUnits = (long)(tx.AmountMajor * 100m);

        // Unit price: tx.UnitPrice is price-per-litre in major units. Convert to minor-per-litre.
        var unitPriceMinorPerLitre = (long)(tx.UnitPrice * 100m);

        var startedAt = DateTimeOffset.TryParse(tx.StartTime, out var s) ? s : rawPayload.ReceivedAt;
        var completedAt = DateTimeOffset.TryParse(tx.EndTime, out var e) ? e : rawPayload.ReceivedAt;

        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new CanonicalTransaction
        {
            FccTransactionId = tx.OrderId,
            SiteCode = rawPayload.SiteCode,
            LegalEntityId = rawPayload.SiteCode,
            PumpNumber = tx.PumpNumber,
            NozzleNumber = tx.NozzleNumber,
            ProductCode = tx.ProductCode,
            VolumeMicrolitres = volumeMicrolitres,
            AmountMinorUnits = amountMinorUnits,
            UnitPriceMinorPerLitre = unitPriceMinorPerLitre,
            CurrencyCode = tx.Currency,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            FiscalReceiptNumber = tx.ReceiptCode,
            FccVendor = "Petronite",
            AttendantId = tx.AttendantId,
            IngestionSource = "FccPush",
            IngestedAt = now,
            UpdatedAt = now,
            RawPayloadJson = rawPayload.RawJson,
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Two-step pre-auth: (1) POST /direct-authorize-requests/create to create the order,
    /// then (2) POST /direct-authorize-requests/authorize to authorize the pump.
    /// On 401, invalidates the OAuth token and retries once.
    /// </remarks>
    public async Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct)
    {
        try
        {
            var nozzleId = await _nozzleResolver.ResolveNozzleIdAsync(
                command.FccPumpNumber, command.FccNozzleNumber, ct);

            // Convert from minor units to major units for the Petronite API.
            var maxAmountMajor = command.RequestedAmountMinorUnits / 100m;

            var createRequest = new PetroniteCreateOrderRequest(
                NozzleId: nozzleId,
                MaxVolumeLitres: 9999m, // No volume limit; amount is the cap.
                MaxAmountMajor: maxAmountMajor,
                Currency: command.Currency,
                ExternalReference: command.PreAuthId);

            // Step 1: Create order.
            var createResponse = await PostWithAuthAsync<PetroniteCreateOrderRequest, PetroniteCreateOrderResponse>(
                "/direct-authorize-requests/create", createRequest, ct);

            if (createResponse is null)
                return new PreAuthResult(false, null, null, "PARSE_ERROR", "Empty Petronite create-order response");

            // Step 2: Authorize pump.
            var authRequest = new PetroniteAuthorizeRequest(OrderId: createResponse.OrderId);

            var authResponse = await PostWithAuthAsync<PetroniteAuthorizeRequest, PetroniteAuthorizeResponse>(
                "/direct-authorize-requests/authorize", authRequest, ct);

            if (authResponse is null)
                return new PreAuthResult(false, createResponse.OrderId, null, "PARSE_ERROR", "Empty Petronite authorize response");

            var accepted = authResponse.Status.Equals("AUTHORIZED", StringComparison.OrdinalIgnoreCase);
            return new PreAuthResult(
                Accepted: accepted,
                FccCorrelationId: authResponse.OrderId,
                FccAuthorizationCode: authResponse.AuthorizationCode,
                ErrorCode: accepted ? null : authResponse.Status,
                ErrorMessage: authResponse.Message);
        }
        catch (FccAdapterException ex)
        {
            _logger.LogWarning(ex, "Petronite pre-auth error (pump {Pump})", command.FccPumpNumber);
            return new PreAuthResult(false, null, null, $"HTTP_{ex.HttpStatusCode}", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Petronite pre-auth transport error (pump {Pump})", command.FccPumpNumber);
            return new PreAuthResult(false, null, null, "TRANSPORT_ERROR", ex.Message);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Petronite does not expose per-pump status. Returns an empty list.
    /// Pump state is inferred from webhooks at a higher layer.
    /// </remarks>
    public Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PumpStatus>>([]);

    /// <inheritdoc/>
    /// <remarks>
    /// Uses GET /nozzles/assigned as a liveness probe.
    /// 5-second hard deadline regardless of the named client's default timeout.
    /// </remarks>
    public async Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var token = await _oauthClient.GetAccessTokenAsync(cts.Token);

            var http = _httpFactory.CreateClient("fcc");
            var baseUri = new Uri(_config.BaseUrl.TrimEnd('/') + "/");
            var requestUri = new Uri(baseUri, "nozzles/assigned");

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Petronite heartbeat timed out");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Petronite heartbeat failed");
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Push-only model: Petronite pushes transactions via webhooks.
    /// FetchTransactionsAsync always returns an empty batch.
    /// </remarks>
    public Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct)
        => Task.FromResult(new TransactionBatch([], null, false));

    /// <inheritdoc/>
    /// <remarks>
    /// Posts /{orderId}/cancel to the Petronite direct-authorize-requests endpoint.
    /// Returns true if cancellation succeeded, false otherwise.
    /// </remarks>
    public async Task<bool> CancelPreAuthAsync(string fccCorrelationId, CancellationToken ct)
    {
        try
        {
            var token = await _oauthClient.GetAccessTokenAsync(ct);

            var http = _httpFactory.CreateClient("fcc");
            var baseUri = new Uri(_config.BaseUrl.TrimEnd('/') + "/");
            var requestUri = new Uri(
                baseUri,
                $"direct-authorize-requests/{Uri.EscapeDataString(fccCorrelationId)}/cancel");

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
                return true;

            var statusCode = (int)response.StatusCode;

            // 404 = order not found or already terminal — treat as idempotent success.
            if (statusCode == 404)
                return false;

            _logger.LogWarning(
                "Petronite cancel pre-auth returned HTTP {StatusCode} for {CorrelationId}",
                statusCode,
                fccCorrelationId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Petronite cancel pre-auth error for {CorrelationId}", fccCorrelationId);
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op — Petronite is push-only; there is no FCC buffer to acknowledge against.
    /// </remarks>
    public Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct)
        => Task.FromResult(true);

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Sends an authenticated POST request with JSON body.
    /// On 401, invalidates the OAuth token and retries once.
    /// </summary>
    private async Task<TResponse?> PostWithAuthAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        CancellationToken ct)
        where TResponse : class
    {
        var response = await SendAuthenticatedAsync(HttpMethod.Post, path, body, ct);

        // 401 retry: invalidate token and retry once.
        if ((int)response.StatusCode == 401)
        {
            response.Dispose();
            await _oauthClient.InvalidateTokenAsync(ct);
            response = await SendAuthenticatedAsync(HttpMethod.Post, path, body, ct);
        }

        using (response)
        {
            ThrowIfHttpError(response, path);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<TResponse>(responseBody, JsonOptions);
        }
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync<TRequest>(
        HttpMethod method,
        string path,
        TRequest? body,
        CancellationToken ct)
    {
        var token = await _oauthClient.GetAccessTokenAsync(ct);

        var http = _httpFactory.CreateClient("fcc");
        var baseUri = new Uri(_config.BaseUrl.TrimEnd('/') + "/");
        var requestUri = new Uri(baseUri, path.TrimStart('/'));

        using var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FccAdapterException(
                $"Petronite {path} transport failure",
                isRecoverable: true,
                ex);
        }
    }

    private static void ThrowIfHttpError(HttpResponseMessage response, string path)
    {
        if (response.IsSuccessStatusCode) return;

        var statusCode = (int)response.StatusCode;
        // 401/403 = auth misconfiguration — non-recoverable
        // 408/429/5xx = transient — recoverable
        var isRecoverable = statusCode is 408 or 429 || statusCode >= 500;

        throw new FccAdapterException(
            $"Petronite {path} returned HTTP {statusCode}",
            isRecoverable,
            httpStatusCode: statusCode);
    }
}
