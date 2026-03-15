using System.Collections.Concurrent;
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
/// Push-only model: transactions arrive via <see cref="PetroniteWebhookListener"/>
/// and are queued internally. <see cref="FetchTransactionsAsync"/> drains that queue
/// so the standard ingestion pipeline buffers them. On the first fetch call the adapter
/// lazily starts the webhook listener and runs <see cref="ReconcileOnStartupAsync"/>.
/// </para>
/// </summary>
public sealed class PetroniteAdapter : IFccAdapter, IAsyncDisposable
{
    /// <inheritdoc />
    public PumpStatusCapability PumpStatusCapability => PumpStatusCapability.Synthesized;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Default port for the local webhook listener when none is configured.</summary>
    private const int DefaultWebhookListenerPort = 8090;

    /// <summary>
    /// Duration after which a pending order found during startup reconciliation is considered
    /// stale and should be auto-cancelled rather than re-adopted.
    /// </summary>
    private static readonly TimeSpan StaleOrderThreshold = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly FccConnectionConfig _config;
    private readonly PetroniteOAuthClient _oauthClient;
    private readonly PetroniteNozzleResolver _nozzleResolver;
    private readonly ILogger<PetroniteAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Active pre-authorizations keyed by Petronite OrderId.
    /// Thread-safe for concurrent NormalizeAsync / SendPreAuthAsync / CancelPreAuthAsync calls.
    /// </summary>
    private readonly ConcurrentDictionary<string, ActivePreAuth> _activePreAuths = new();

    // ── Webhook listener (PN-4.1) ───────────────────────────────────────────

    /// <summary>
    /// Queue of raw webhook payloads received by the listener, drained by
    /// <see cref="FetchTransactionsAsync"/>. Same pattern as Radix push listener.
    /// </summary>
    private readonly ConcurrentQueue<RawPayloadEnvelope> _webhookQueue = new();

    /// <summary>Local HTTP listener for Petronite webhook callbacks.</summary>
    private PetroniteWebhookListener? _webhookListener;

    /// <summary>Guards one-time initialization (listener start + reconciliation).</summary>
    private volatile bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public PetroniteAdapter(
        IHttpClientFactory httpFactory,
        FccConnectionConfig config,
        PetroniteOAuthClient oauthClient,
        PetroniteNozzleResolver nozzleResolver,
        ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _config = config;
        _oauthClient = oauthClient;
        _nozzleResolver = nozzleResolver;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PetroniteAdapter>();
    }

    // ── PN-2.1: Webhook Normalization ────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Parses a Petronite webhook JSON payload into a canonical transaction.
    /// Volume is converted from litres (decimal) to microlitres (long).
    /// Amount is converted from major currency units (decimal) to minor units (long).
    /// If PaymentMethod == "PUMA_ORDER", correlates with an active pre-auth (PN-3.5).
    /// </remarks>
    public Task<CanonicalTransaction> NormalizeAsync(RawPayloadEnvelope rawPayload, CancellationToken ct)
    {
        var webhook = JsonSerializer.Deserialize<PetroniteWebhookPayload>(rawPayload.RawJson, JsonOptions)
            ?? throw new FccAdapterException(
                "Petronite normalization: null result from deserialization",
                isRecoverable: false);

        if (!webhook.EventType.Equals("transaction.completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new FccAdapterException(
                $"Petronite normalization: unsupported event type '{webhook.EventType}' (expected 'transaction.completed')",
                isRecoverable: false);
        }

        var tx = webhook.Transaction
            ?? throw new FccAdapterException(
                "Petronite normalization: webhook payload has no transaction data",
                isRecoverable: false);

        // Resolve nozzleId -> canonical pump/nozzle via the cached nozzle map.
        // Falls back to the webhook's pumpNumber/nozzleNumber if the resolver has no mapping
        // (e.g., resolver hasn't refreshed yet).
        int pumpNumber = tx.PumpNumber;
        int nozzleNumber = tx.NozzleNumber;
        try
        {
            var snapshot = _nozzleResolver.GetCurrentSnapshot();
            if (snapshot.TryGetValue(tx.NozzleId, out var canonical))
            {
                pumpNumber = canonical.PumpNumber;
                nozzleNumber = canonical.NozzleNumber;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Petronite normalization: nozzle reverse-map failed for '{NozzleId}', using webhook values", tx.NozzleId);
        }

        // Convert volume from litres (decimal) to microlitres (long) using decimal arithmetic.
        var volumeMicrolitres = (long)(tx.VolumeLitres * 1_000_000m);

        // Convert amount from major units (e.g. dollars) to minor units (e.g. cents).
        // Currency decimals are configurable; default is 2 (x100).
        int currencyDecimals = GetCurrencyDecimals(tx.Currency);
        decimal currencyMultiplier = DecimalPow10(currencyDecimals);
        var amountMinorUnits = (long)(tx.AmountMajor * currencyMultiplier);

        // Unit price: tx.UnitPrice is price-per-litre in major units. Convert to minor-per-litre.
        var unitPriceMinorPerLitre = (long)(tx.UnitPrice * currencyMultiplier);

        var startedAt = DateTimeOffset.TryParse(tx.StartTime, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var s) ? s : rawPayload.ReceivedAt;
        var completedAt = DateTimeOffset.TryParse(tx.EndTime, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var e) ? e : rawPayload.ReceivedAt;

        // Dedup key: {siteCode}-{OrderId}
        var dedupKey = $"{rawPayload.SiteCode}-{tx.OrderId}";

        var now = DateTimeOffset.UtcNow;

        // PN-3.5: Pre-Auth / Dispense Correlation
        string? correlationId = null;
        string? odooOrderId = null;
        string? preAuthId = null;

        if (tx.PaymentMethod.Equals("PUMA_ORDER", StringComparison.OrdinalIgnoreCase))
        {
            if (_activePreAuths.TryRemove(tx.OrderId, out var preAuth))
            {
                correlationId = preAuth.OrderId;
                odooOrderId = preAuth.OdooOrderId;
                preAuthId = preAuth.PreAuthId;

                _logger.LogInformation(
                    "Petronite normalization: correlated transaction {OrderId} with pre-auth (OdooOrderId={OdooOrderId})",
                    tx.OrderId, odooOrderId);
            }
            else
            {
                _logger.LogWarning(
                    "Petronite normalization: PUMA_ORDER transaction {OrderId} has no matching active pre-auth",
                    tx.OrderId);
                // Still set correlationId to OrderId for traceability
                correlationId = tx.OrderId;
            }
        }

        return Task.FromResult(new CanonicalTransaction
        {
            FccTransactionId = dedupKey,
            SiteCode = rawPayload.SiteCode,
            LegalEntityId = rawPayload.SiteCode,
            PumpNumber = pumpNumber,
            NozzleNumber = nozzleNumber,
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
            CorrelationId = correlationId,
            OdooOrderId = odooOrderId,
            PreAuthId = preAuthId,
        });
    }

    /// <summary>
    /// Purges pre-auth entries older than <see cref="StaleOrderThreshold"/> to prevent memory leaks.
    /// Called during each fetch cycle.
    /// </summary>
    private void PurgeStalePreAuths()
    {
        var cutoff = DateTimeOffset.UtcNow - StaleOrderThreshold;
        foreach (var kvp in _activePreAuths)
        {
            if (kvp.Value.CreatedAt < cutoff)
            {
                if (_activePreAuths.TryRemove(kvp.Key, out _))
                    _logger.LogWarning("Purged stale pre-auth: OrderId={OrderId}, age > {TtlMinutes}m", kvp.Key, StaleOrderThreshold.TotalMinutes);
            }
        }
    }

    // ── PN-2.2 + PN-4.1: Fetch Transactions (drain webhook queue) ──────────

    /// <inheritdoc/>
    /// <remarks>
    /// On the first call, lazily starts the <see cref="PetroniteWebhookListener"/>
    /// and runs <see cref="ReconcileOnStartupAsync"/> (PN-3.4). Subsequent calls
    /// drain the webhook queue into a <see cref="TransactionBatch"/>.
    /// Same pattern as the Radix push listener.
    /// </remarks>
    public async Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        var cutoff = DateTimeOffset.UtcNow - StaleOrderThreshold;
        foreach (var kvp in _activePreAuths)
        {
            if (kvp.Value.CreatedAt < cutoff && _activePreAuths.TryRemove(kvp.Key, out _))
            {
                _logger.LogWarning(
                    "Purged stale pre-auth: OrderId={OrderId}, age > {TtlMinutes}m",
                    kvp.Key,
                    StaleOrderThreshold.TotalMinutes);
            }
        }

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

    // ── Lazy initialization (webhook listener + reconciliation) ──────────────

    /// <summary>
    /// One-time startup: creates and starts the <see cref="PetroniteWebhookListener"/>
    /// then runs <see cref="ReconcileOnStartupAsync"/> to recover pending pre-auths.
    /// Thread-safe via <see cref="_initLock"/>; no-op on subsequent calls.
    /// </summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            // Start the webhook listener (PN-4.1).
            var port = _config.WebhookListenerPort ?? DefaultWebhookListenerPort;
            try
            {
                _webhookListener = new PetroniteWebhookListener(
                    port, _config, _loggerFactory.CreateLogger<PetroniteWebhookListener>());

                _webhookListener.Start(OnWebhookReceivedAsync, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Petronite webhook listener failed to start on port {Port}; "
                    + "push transactions will not be received until the port is available", port);
                // Non-fatal: adapter continues but push won't work.
                // Do NOT set _initialized so the next call retries listener startup.
                return;
            }

            // Run startup reconciliation (PN-3.4).
            try
            {
                await ReconcileOnStartupAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Petronite startup reconciliation failed (non-fatal)");
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Webhook callback: enqueues received payloads for the next
    /// <see cref="FetchTransactionsAsync"/> drain cycle.
    /// </summary>
    private Task OnWebhookReceivedAsync(RawPayloadEnvelope payload, CancellationToken ct)
    {
        _webhookQueue.Enqueue(payload);
        _logger.LogDebug(
            "Petronite webhook payload enqueued (queue size: {Size})", _webhookQueue.Count);
        return Task.CompletedTask;
    }

    // ── PN-2.2: Pump Status (synthesized from nozzle assignments) ────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Synthesizes pump status from nozzle assignments and cross-references
    /// with active pre-auths for AUTHORIZED state.
    /// GET /nozzles/assigned -> map each to PumpStatus.
    /// </remarks>
    public async Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct)
    {
        try
        {
            var assignments = await FetchNozzleAssignmentsAsync(ct);
            if (assignments is null || assignments.Count == 0)
                return [];

            // Build a set of nozzleIds that have active pre-auths for cross-referencing.
            var authorizedNozzles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preAuth in _activePreAuths.Values)
            {
                authorizedNozzles.Add(preAuth.NozzleId);
            }

            var now = DateTimeOffset.UtcNow;
            var result = new List<PumpStatus>(assignments.Count);

            foreach (var nozzle in assignments)
            {
                // Determine state: if nozzle has an active pre-auth, mark as Authorized.
                // Otherwise, map the Petronite status string to a canonical PumpState.
                var state = authorizedNozzles.Contains(nozzle.NozzleId)
                    ? PumpState.Authorized
                    : MapNozzleStatus(nozzle.Status);

                result.Add(new PumpStatus
                {
                    SiteCode = _config.SiteCode,
                    PumpNumber = nozzle.PumpNumber,
                    NozzleNumber = nozzle.NozzleNumber,
                    State = state,
                    ObservedAtUtc = now,
                    Source = PumpStatusSource.EdgeSynthesized,
                    ProductCode = nozzle.ProductCode,
                    ProductName = nozzle.ProductName,
                    FccStatusCode = nozzle.Status,
                });
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Petronite GetPumpStatusAsync failed, returning empty list");
            return [];
        }
    }

    // ── PN-1.5: Heartbeat ────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Uses GET /nozzles/assigned as a liveness probe.
    /// 5-second hard deadline regardless of the named client's default timeout.
    /// On 401: invalidates OAuth token and retries once.
    /// Never throws - returns true/false.
    /// </remarks>
    public async Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var success = await TryHeartbeatOnceAsync(cts.Token);
            if (success)
                return true;

            // Retry once after invalidating the token (handles 401).
            _logger.LogDebug("Petronite heartbeat failed, invalidating token and retrying");
            await _oauthClient.InvalidateTokenAsync(cts.Token);
            return await TryHeartbeatOnceAsync(cts.Token);
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

    // ── PN-3.1 + PN-3.2: Two-Step Pre-Auth ──────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Two-step pre-auth: (1) POST /direct-authorize-requests/create to create the order,
    /// then (2) POST /direct-authorize-requests/authorize to authorize the pump.
    /// On 401, invalidates the OAuth token and retries once.
    /// Tracks OrderId in the active pre-auth map for later correlation.
    /// </remarks>
    public async Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct)
    {
        try
        {
            var nozzleId = await _nozzleResolver.ResolveNozzleIdAsync(
                command.FccPumpNumber, command.FccNozzleNumber, ct);

            // Convert from minor units to major units for the Petronite API.
            int currencyDecimals = GetCurrencyDecimals(command.Currency);
            decimal currencyDivisor = DecimalPow10(currencyDecimals);
            var maxAmountMajor = command.RequestedAmountMinorUnits / currencyDivisor;

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

            _logger.LogInformation(
                "Petronite order created: OrderId={OrderId}, Status={Status} (pump {Pump} nozzle {Nozzle})",
                createResponse.OrderId, createResponse.Status, command.FccPumpNumber, command.FccNozzleNumber);

            // Step 2: Authorize pump.
            var authRequest = new PetroniteAuthorizeRequest(OrderId: createResponse.OrderId);

            HttpResponseMessage? authRawResponse = null;
            try
            {
                authRawResponse = await SendAuthenticatedAsync(HttpMethod.Post,
                    "/direct-authorize-requests/authorize", authRequest, ct);

                // Handle 401 retry.
                if ((int)authRawResponse.StatusCode == 401)
                {
                    authRawResponse.Dispose();
                    await _oauthClient.InvalidateTokenAsync(ct);
                    authRawResponse = await SendAuthenticatedAsync(HttpMethod.Post,
                        "/direct-authorize-requests/authorize", authRequest, ct);
                }

                // Handle 400 (nozzle not lifted) -> return DECLINED.
                if ((int)authRawResponse.StatusCode == 400)
                {
                    var errorBody = await authRawResponse.Content.ReadAsStringAsync(ct);
                    _logger.LogWarning(
                        "Petronite authorize returned 400 for OrderId={OrderId}: {Body}",
                        createResponse.OrderId, errorBody);

                    var errorResponse = TryDeserialize<PetroniteErrorResponse>(errorBody);
                    return new PreAuthResult(
                        Accepted: false,
                        FccCorrelationId: createResponse.OrderId,
                        FccAuthorizationCode: null,
                        ErrorCode: "DECLINED",
                        ErrorMessage: errorResponse?.Message ?? "Nozzle not lifted or pump not ready");
                }

                ThrowIfHttpError(authRawResponse, "/direct-authorize-requests/authorize");

                var authResponseBody = await authRawResponse.Content.ReadAsStringAsync(ct);
                var authResponse = JsonSerializer.Deserialize<PetroniteAuthorizeResponse>(authResponseBody, JsonOptions);

                if (authResponse is null)
                    return new PreAuthResult(false, createResponse.OrderId, null, "PARSE_ERROR", "Empty Petronite authorize response");

                var accepted = authResponse.Status.Equals("AUTHORIZED", StringComparison.OrdinalIgnoreCase);

                if (accepted)
                {
                    // Track in the active pre-auth map for correlation on webhook arrival.
                    var activePreAuth = new ActivePreAuth(
                        OrderId: authResponse.OrderId,
                        NozzleId: nozzleId,
                        PumpNumber: command.FccPumpNumber,
                        OdooOrderId: command.FccCorrelationId,
                        PreAuthId: command.PreAuthId,
                        CreatedAt: DateTimeOffset.UtcNow);

                    _activePreAuths[authResponse.OrderId] = activePreAuth;

                    _logger.LogInformation(
                        "Petronite pump authorized: OrderId={OrderId}, AuthCode={AuthCode} (pump {Pump})",
                        authResponse.OrderId, authResponse.AuthorizationCode, command.FccPumpNumber);
                }

                return new PreAuthResult(
                    Accepted: accepted,
                    FccCorrelationId: authResponse.OrderId,
                    FccAuthorizationCode: authResponse.AuthorizationCode,
                    ErrorCode: accepted ? null : authResponse.Status,
                    ErrorMessage: authResponse.Message);
            }
            finally
            {
                authRawResponse?.Dispose();
            }
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

    // ── PN-3.3: Cancel Pre-Auth ──────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Posts /{orderId}/cancel to the Petronite direct-authorize-requests endpoint.
    /// Idempotent: 404 (already cancelled or not found) is treated as success.
    /// Removes the order from the active pre-auth map.
    /// </remarks>
    public async Task<bool> CancelPreAuthAsync(string fccCorrelationId, CancellationToken ct)
    {
        try
        {
            // Always remove from active map, regardless of API outcome.
            _activePreAuths.TryRemove(fccCorrelationId, out _);

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
            {
                _logger.LogInformation("Petronite pre-auth cancelled: OrderId={OrderId}", fccCorrelationId);
                return true;
            }

            var statusCode = (int)response.StatusCode;

            // 401 retry: invalidate and try once more.
            if (statusCode == 401)
            {
                await _oauthClient.InvalidateTokenAsync(ct);
                var retryToken = await _oauthClient.GetAccessTokenAsync(ct);

                using var retryRequest = new HttpRequestMessage(HttpMethod.Post, requestUri);
                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", retryToken);

                using var retryResponse = await http.SendAsync(retryRequest, ct);
                if (retryResponse.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Petronite pre-auth cancelled (after 401 retry): OrderId={OrderId}", fccCorrelationId);
                    return true;
                }

                statusCode = (int)retryResponse.StatusCode;
            }

            // 404 = order not found or already terminal -- treat as idempotent success.
            if (statusCode == 404)
            {
                _logger.LogDebug("Petronite cancel returned 404 for {OrderId} (already cancelled or not found)", fccCorrelationId);
                return true;
            }

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

    // ── AcknowledgeTransactionsAsync (no-op) ─────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// No-op -- Petronite is push-only; there is no FCC buffer to acknowledge against.
    /// </remarks>
    public Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct)
        => Task.FromResult(true);

    // ── PN-3.4: Startup Reconciliation ───────────────────────────────────────

    /// <summary>
    /// Reconciles pending pre-authorizations on adapter startup.
    /// Fetches GET /direct-authorize-requests/pending, then:
    ///   - Orders older than 30 minutes: auto-cancelled
    ///   - Recent orders: re-adopted into the active pre-auth map
    /// Non-fatal: logs errors and continues. Call this during service startup.
    /// </summary>
    public async Task ReconcileOnStartupAsync(CancellationToken ct)
    {
        _logger.LogInformation("Petronite startup reconciliation: fetching pending orders...");

        List<PetronitePendingOrder>? pendingOrders;
        try
        {
            pendingOrders = await GetWithAuthAsync<List<PetronitePendingOrder>>(
                "/direct-authorize-requests/pending", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Petronite startup reconciliation: failed to fetch pending orders, skipping");
            return;
        }

        if (pendingOrders is null || pendingOrders.Count == 0)
        {
            _logger.LogInformation("Petronite startup reconciliation: no pending orders found");
            return;
        }

        _logger.LogInformation("Petronite startup reconciliation: found {Count} pending order(s)", pendingOrders.Count);

        var now = DateTimeOffset.UtcNow;
        int cancelled = 0;
        int adopted = 0;

        foreach (var order in pendingOrders)
        {
            try
            {
                var createdAt = DateTimeOffset.TryParse(order.CreatedAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var c)
                    ? c
                    : now; // If we can't parse, treat as recent (safer to adopt than cancel).

                if (now - createdAt > StaleOrderThreshold)
                {
                    // Stale order: auto-cancel.
                    _logger.LogInformation(
                        "Petronite reconciliation: cancelling stale order {OrderId} (created at {CreatedAt})",
                        order.OrderId, order.CreatedAt);

                    await CancelPreAuthAsync(order.OrderId, ct);
                    cancelled++;
                }
                else
                {
                    // Recent order: re-adopt into active pre-auth map.
                    // We don't have the original OdooOrderId or PreAuthId, so set them null.
                    // The nozzle resolver will map nozzleId -> pump/nozzle.
                    int pumpNumber = 0;
                    try
                    {
                        var canonical = await _nozzleResolver.ResolveCanonicalAsync(order.NozzleId, ct);
                        pumpNumber = canonical.PumpNumber;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Petronite reconciliation: could not resolve nozzle {NozzleId}", order.NozzleId);
                    }

                    var activePreAuth = new ActivePreAuth(
                        OrderId: order.OrderId,
                        NozzleId: order.NozzleId,
                        PumpNumber: pumpNumber,
                        OdooOrderId: null,
                        PreAuthId: null,
                        CreatedAt: createdAt);

                    _activePreAuths[order.OrderId] = activePreAuth;
                    adopted++;

                    _logger.LogInformation(
                        "Petronite reconciliation: re-adopted order {OrderId} (created at {CreatedAt}, pump {Pump})",
                        order.OrderId, order.CreatedAt, pumpNumber);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Petronite reconciliation: error processing order {OrderId}, skipping", order.OrderId);
            }
        }

        _logger.LogInformation(
            "Petronite startup reconciliation complete: {Cancelled} cancelled, {Adopted} adopted",
            cancelled, adopted);
    }

    // ── PN-4.2: Ingestion Mode Validation ────────────────────────────────────

    /// <summary>
    /// Validates the ingestion mode for Petronite. Petronite is push-only via webhooks.
    /// </summary>
    /// <param name="mode">The configured ingestion mode.</param>
    /// <exception cref="FccAdapterException">Thrown when PULL mode is configured (not supported).</exception>
    public void ValidateIngestionMode(IngestionMode mode)
    {
        switch (mode)
        {
            case IngestionMode.Relay:
                throw new FccAdapterException(
                    "Petronite does not support PULL/Relay ingestion mode. "
                    + "Petronite is push-only via webhooks. Configure IngestionMode=CloudDirect or BufferAlways.",
                    isRecoverable: false);

            case IngestionMode.BufferAlways:
                _logger.LogWarning(
                    "Petronite configured with BufferAlways mode. "
                    + "This is supported but transactions still arrive via webhooks, not polling. "
                    + "Ensure the webhook listener is running.");
                break;

            case IngestionMode.CloudDirect:
                _logger.LogInformation("Petronite ingestion mode: CloudDirect (push-only via webhooks)");
                break;

            default:
                _logger.LogWarning("Petronite: unknown ingestion mode '{Mode}'", mode);
                break;
        }
    }

    /// <summary>
    /// Returns adapter metadata for diagnostics and configuration reporting.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetAdapterMetadata() => new Dictionary<string, string>
    {
        ["vendor"] = "Petronite",
        ["protocol"] = "REST+OAuth2",
        ["ingestionModel"] = "PUSH-only",
        ["webhookListening"] = (_webhookListener?.IsListening ?? false).ToString(),
        ["webhookQueueSize"] = _webhookQueue.Count.ToString(),
        ["preAuthModel"] = "two-step (create+authorize)",
        ["activePreAuths"] = _activePreAuths.Count.ToString(),
    };

    /// <summary>
    /// Returns the number of active pre-authorizations currently tracked.
    /// </summary>
    public int ActivePreAuthCount => _activePreAuths.Count;

    /// <summary>Current webhook queue depth (for diagnostics).</summary>
    public int WebhookQueueSize => _webhookQueue.Count;

    /// <summary>Whether the webhook listener is currently accepting connections.</summary>
    public bool IsWebhookListening => _webhookListener?.IsListening ?? false;

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    /// <summary>
    /// Stops the webhook listener gracefully on adapter shutdown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_webhookListener is not null)
        {
            await _webhookListener.StopAsync();
            _webhookListener.Dispose();
            _webhookListener = null;
        }

        _initLock.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Performs a single heartbeat attempt (GET /nozzles/assigned).
    /// Returns true on 2xx, false otherwise.
    /// </summary>
    private async Task<bool> TryHeartbeatOnceAsync(CancellationToken ct)
    {
        var token = await _oauthClient.GetAccessTokenAsync(ct);

        var http = _httpFactory.CreateClient("fcc");
        var baseUri = new Uri(_config.BaseUrl.TrimEnd('/') + "/");
        var requestUri = new Uri(baseUri, "nozzles/assigned");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Fetches nozzle assignments from GET /nozzles/assigned with auth.
    /// </summary>
    private async Task<List<PetroniteNozzleAssignment>?> FetchNozzleAssignmentsAsync(CancellationToken ct)
    {
        return await GetWithAuthAsync<List<PetroniteNozzleAssignment>>("nozzles/assigned", ct);
    }

    /// <summary>
    /// Sends an authenticated GET request and deserializes the response.
    /// On 401, invalidates the OAuth token and retries once.
    /// </summary>
    private async Task<TResponse?> GetWithAuthAsync<TResponse>(string path, CancellationToken ct)
        where TResponse : class
    {
        var response = await SendAuthenticatedAsync<object>(HttpMethod.Get, path, null, ct);

        // 401 retry: invalidate token and retry once.
        if ((int)response.StatusCode == 401)
        {
            response.Dispose();
            await _oauthClient.InvalidateTokenAsync(ct);
            response = await SendAuthenticatedAsync<object>(HttpMethod.Get, path, null, ct);
        }

        using (response)
        {
            ThrowIfHttpError(response, path);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<TResponse>(responseBody, JsonOptions);
        }
    }

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
        // 401/403 = auth misconfiguration -- non-recoverable
        // 408/429/5xx = transient -- recoverable
        var isRecoverable = statusCode is 408 or 429 || statusCode >= 500;

        throw new FccAdapterException(
            $"Petronite {path} returned HTTP {statusCode}",
            isRecoverable,
            httpStatusCode: statusCode);
    }

    /// <summary>
    /// Maps Petronite nozzle status string to canonical PumpState.
    /// </summary>
    private static PumpState MapNozzleStatus(string status) => status?.ToUpperInvariant() switch
    {
        "IDLE" => PumpState.Idle,
        "AVAILABLE" => PumpState.Idle,
        "CALLING" => PumpState.Calling,
        "NOZZLE_LIFTED" => PumpState.Calling,
        "DISPENSING" => PumpState.Dispensing,
        "AUTHORIZED" => PumpState.Authorized,
        "COMPLETED" => PumpState.Completed,
        "ERROR" => PumpState.Error,
        "OFFLINE" => PumpState.Offline,
        "PAUSED" => PumpState.Paused,
        _ => PumpState.Unknown,
    };

    /// <summary>
    /// Returns the number of decimal places for the given ISO 4217 currency code.
    /// Defaults to 2 for unrecognized currencies.
    /// </summary>
    private static int GetCurrencyDecimals(string currencyCode) => currencyCode?.ToUpperInvariant() switch
    {
        "BHD" or "IQD" or "JOD" or "KWD" or "LYD" or "OMR" or "TND" => 3,
        "BIF" or "CLP" or "DJF" or "GNF" or "ISK" or "JPY" or "KMF"
            or "KRW" or "PYG" or "RWF" or "UGX" or "UYI" or "VND"
            or "VUV" or "XAF" or "XOF" or "XPF" => 0,
        _ => 2,
    };

    /// <summary>
    /// Returns 10^n as decimal without floating point.
    /// </summary>
    private static decimal DecimalPow10(int n)
    {
        decimal result = 1m;
        for (int i = 0; i < n; i++)
            result *= 10m;
        return result;
    }

    /// <summary>
    /// Tries to deserialize JSON, returning null on failure.
    /// </summary>
    private static T? TryDeserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // ── ActivePreAuth record ─────────────────────────────────────────────────

    /// <summary>
    /// Internal record tracking an active pre-authorization for correlation with
    /// incoming webhook transactions.
    /// </summary>
    private sealed record ActivePreAuth(
        string OrderId,
        string NozzleId,
        int PumpNumber,
        string? OdooOrderId,
        string? PreAuthId,
        DateTimeOffset CreatedAt);
}
