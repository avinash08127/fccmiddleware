namespace FccDesktopAgent.Core.Adapter.Radix;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.Extensions.Logging;

/// <summary>
/// Radix FCC adapter — HTTP/XML stateless protocol on dual ports.
///
/// Communicates with the FCC over station LAN using HTTP POST with XML bodies:
/// <list type="bullet">
///   <item><b>Auth port P</b> (from config AuthPort) — external authorization (pre-auth)</item>
///   <item><b>Transaction port P+1</b> — transaction management, products, day close, ATG, CSR</item>
///   <item><b>Signing</b> — SHA-1 hash of XML body + shared secret password</item>
///   <item><b>Heartbeat</b> — CMD_CODE=55 (product/price read) — no dedicated endpoint</item>
///   <item><b>Fetch</b> — FIFO drain loop: CMD_CODE=10 (request) then CMD_CODE=201 (ACK) then repeat</item>
///   <item><b>Pre-auth</b> — AUTH_DATA XML to auth port P</item>
///   <item><b>Pump status</b> — Not supported by Radix protocol</item>
/// </list>
///
/// Implements Phase 2 tasks:
///   RX-3.2 — Transaction fetch (FIFO drain)
///   RX-3.4 — Normalization (volume/amount via decimal, timestamps, dedup key)
///   RX-4.2 — Pre-auth (AUTH_DATA to auth port with ACKCODE mapping)
///   RX-5.1 — Push listener (mode management, heartbeat)
/// </summary>
public sealed class RadixAdapter : IFccAdapter
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly FccConnectionConfig _config;
    private readonly ILogger<RadixAdapter> _logger;
    private readonly string _siteCode;
    private readonly string _legalEntityId;
    private readonly string _currencyCode;
    private readonly string _timezone;
    private readonly int _pumpNumberOffset;
    private readonly IReadOnlyDictionary<string, string>? _productCodeMapping;

    // ── Internal state ──────────────────────────────────────────────────────

    /// <summary>Sequential token counter (1-65535), wraps at 65536. Thread-safe via Interlocked. Starts at 1 because TOKEN=0 means "Normal Order" (no pre-auth) in the Radix protocol.</summary>
    private int _tokenCounter = 1;

    /// <summary>
    /// Cached current FCC transaction transfer mode.
    ///   -1 = unknown (not yet set or reset after connectivity loss)
    ///    0 = OFF (transaction transfer disabled)
    ///    1 = ON_DEMAND (pull mode)
    ///    2 = UNSOLICITED (push mode)
    /// </summary>
    private volatile int _currentMode = ModeUnknown;

    /// <summary>Active pre-auth entries keyed by TOKEN for later transaction correlation.</summary>
    private readonly ConcurrentDictionary<int, ActivePreAuth> _activePreAuths = new();

    /// <summary>
    /// Purges pre-auth entries older than <see cref="PreAuthTtl"/> to prevent memory leaks.
    /// Called during each fetch cycle. Entries can become stale when the FCC goes offline,
    /// the customer walks away, or a dispense never occurs.
    /// </summary>
    private void PurgeStalePreAuths()
    {
        var cutoff = DateTimeOffset.UtcNow - PreAuthTtl;
        foreach (var kvp in _activePreAuths)
        {
            if (kvp.Value.CreatedAt < cutoff)
            {
                if (_activePreAuths.TryRemove(kvp.Key, out _))
                    _logger.LogDebug("Purged stale pre-auth: token={Token}, age > {TtlMinutes}m", kvp.Key, PreAuthTtl.TotalMinutes);
            }
        }
    }

    // ── Pump address maps (lazy-parsed from config JSON) ────────────────────

    /// <summary>
    /// Parsed pump address map: canonical pump number -> (PumpAddr, Fp).
    /// Lazily parsed from config.FccPumpAddressMap JSON string.
    /// </summary>
    private readonly Lazy<Dictionary<int, (int PumpAddr, int Fp)>> _pumpAddressMap;

    /// <summary>
    /// Reverse pump address map: "PUMP_ADDR-FP" -> canonical pump number.
    /// Used during normalization to resolve FCC-native addresses back to canonical numbers.
    /// </summary>
    private readonly Lazy<Dictionary<string, int>> _reversePumpAddressMap;

    // ── Push listener (RX-5.1) ─────────────────────────────────────────────

    /// <summary>
    /// Unsolicited push listener for PUSH/HYBRID ingestion modes.
    /// When non-null, the FDC is configured to push transactions to this
    /// listener's HTTP endpoint instead of (or in addition to) the agent polling.
    /// </summary>
    private RadixPushListener? _pushListener;

    /// <summary>Push listener port = AuthPort + 2.</summary>
    private int PushListenerPort => (_config.AuthPort ?? 0) + 2;

    // ── Derived config helpers ──────────────────────────────────────────────

    /// <summary>Transaction management port = AuthPort + 1.</summary>
    private int TransactionPort => (_config.AuthPort ?? 0) + 1;

    /// <summary>Authorization port = AuthPort.</summary>
    private int AuthPort => _config.AuthPort ?? 0;

    /// <summary>Shared secret for SHA-1 message signing.</summary>
    private string SharedSecret => _config.SharedSecret ?? string.Empty;

    /// <summary>Unique Station Number sent as USN-Code HTTP header.</summary>
    private int UsnCode => _config.UsnCode ?? 0;

    /// <summary>Base host address extracted from BaseUrl.</summary>
    private string HostAddress
    {
        get
        {
            try { return new Uri(_config.BaseUrl).Host; }
            catch { return _config.BaseUrl; }
        }
    }

    /// <summary>Transaction port URL.</summary>
    private string TransactionPortUrl => $"http://{HostAddress}:{TransactionPort}";

    /// <summary>Auth port URL.</summary>
    private string AuthPortUrl => $"http://{HostAddress}:{AuthPort}";

    /// <summary>Currency decimal factor for converting amount strings to minor units.</summary>
    private static readonly decimal CurrencyDecimalFactor = 100m;

    /// <summary>Volume conversion factor: 1 litre = 1,000,000 microlitres.</summary>
    private static readonly decimal MicrolitresPerLitre = 1_000_000m;

    public RadixAdapter(
        IHttpClientFactory httpFactory,
        FccConnectionConfig config,
        ILogger<RadixAdapter> logger,
        string siteCode = "",
        string legalEntityId = "",
        string currencyCode = "TZS",
        string timezone = "Africa/Dar_es_Salaam",
        int pumpNumberOffset = 0,
        IReadOnlyDictionary<string, string>? productCodeMapping = null)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
        _siteCode = string.IsNullOrWhiteSpace(siteCode) ? config.SiteCode : siteCode;
        _legalEntityId = legalEntityId;
        _currencyCode = currencyCode;
        _timezone = timezone;
        _pumpNumberOffset = pumpNumberOffset;
        _productCodeMapping = productCodeMapping;

        _pumpAddressMap = new Lazy<Dictionary<int, (int PumpAddr, int Fp)>>(
            () => ParsePumpAddressMap(_config.FccPumpAddressMap));

        _reversePumpAddressMap = new Lazy<Dictionary<string, int>>(() =>
        {
            var reverse = new Dictionary<string, int>();
            foreach (var (pumpNumber, entry) in _pumpAddressMap.Value)
            {
                reverse[$"{entry.PumpAddr}-{entry.Fp}"] = pumpNumber;
            }
            return reverse;
        });
    }

    // =====================================================================
    // IFccAdapter — FetchTransactionsAsync (RX-3.2, RX-5.1)
    // =====================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// Supports three ingestion strategies based on config:
    ///
    /// <b>PULL (default / Relay / CloudDirect):</b>
    /// 1. Ensure ON_DEMAND mode (CMD_CODE=20 if not cached)
    /// 2. FIFO drain loop: CMD_CODE=10 -> parse -> ACK CMD_CODE=201 -> repeat
    /// 3. RESP_CODE=205: FIFO empty -> break
    ///
    /// <b>PUSH (BufferAlways — push-capable):</b>
    /// 1. Start RadixPushListener on port P+2
    /// 2. Set FDC to UNSOLICITED mode (CMD_CODE=20, MODE=2)
    /// 3. Drain the push listener's queue of received transactions
    ///
    /// Pull-mode ACK is sent inline during fetch loop — AcknowledgeTransactionsAsync() is a no-op.
    /// Push transactions are ACKed by the listener's HTTP response.
    /// </remarks>
    public async Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct)
    {
        PurgeStalePreAuths();

        var limit = Math.Clamp(cursor.MaxCount, 1, MaxFetchLimit);
        var records = new List<RawPayloadEnvelope>();

        try
        {
            // Determine ingestion strategy from config
            // BufferAlways is used for push-capable sites where the FDC should push to us
            var isPushCapable = _config.ConnectionProtocol?.Equals("PUSH", StringComparison.OrdinalIgnoreCase) == true;

            if (isPushCapable)
            {
                // PUSH mode — start listener and set UNSOLICITED mode
                if (!await EnsurePushListenerRunningAsync(ct).ConfigureAwait(false))
                {
                    _logger.LogWarning("FetchTransactions: push listener startup failed, falling back to pull");
                    return await FetchTransactionsPullAsync(limit, ct).ConfigureAwait(false);
                }

                // Drain pushed transactions
                var pushed = CollectPushedEnvelopes(limit);
                records.AddRange(pushed);

                var hasMore = _pushListener?.QueueSize > 0;
                return new TransactionBatch(records, null, hasMore);
            }

            // Standard PULL mode
            return await FetchTransactionsPullAsync(limit, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Preserve cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FetchTransactions: {ErrorType}: {Message}",
                ex.GetType().Name, ex.Message);
            ResetModeState();
            return new TransactionBatch(records, null, false);
        }
    }

    /// <summary>
    /// Pull-mode transaction fetch via FIFO drain (original logic).
    /// 1. Ensure ON_DEMAND mode
    /// 2. Loop: CMD_CODE=10 -> parse -> ACK -> repeat until FIFO empty or limit reached
    /// </summary>
    private async Task<TransactionBatch> FetchTransactionsPullAsync(int limit, CancellationToken ct)
    {
        var records = new List<RawPayloadEnvelope>();

        // Step 1: Ensure ON_DEMAND mode
        if (!await EnsureModeAsync(ModeOnDemand, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("FetchTransactionsPull: failed to set ON_DEMAND mode");
            return new TransactionBatch(records, null, false);
        }

        // Step 2: FIFO drain loop
        for (var i = 0; i < limit; i++)
        {
            ct.ThrowIfCancellationRequested();

            var token = NextToken();

            // Send CMD_CODE=10 — request next transaction
            var requestBody = RadixXmlBuilder.BuildTransactionRequest(UsnCode, token, SharedSecret);
            var responseBody = await PostXmlAsync(
                TransactionPortUrl,
                requestBody,
                RadixXmlBuilder.OperationTransaction,
                ct).ConfigureAwait(false);

            if (responseBody is null)
            {
                _logger.LogWarning("FetchTransactionsPull: HTTP request failed");
                ResetModeState();
                return new TransactionBatch(records, null, false);
            }

            var txnResp = RadixXmlParser.ParseTransactionResponse(responseBody);
            if (txnResp is null)
            {
                _logger.LogWarning("FetchTransactionsPull: failed to parse response XML");
                return new TransactionBatch(records, null, false);
            }

            switch (txnResp.RespCode)
            {
                case RespCodeSuccess:
                {
                    // Transaction available — wrap as RawPayloadEnvelope
                    if (txnResp.Transaction is not null)
                    {
                        var envelope = new RawPayloadEnvelope(
                            FccVendor: Vendor,
                            SiteCode: _siteCode,
                            RawJson: responseBody,
                            ReceivedAt: DateTimeOffset.UtcNow);

                        records.Add(envelope);
                    }

                    // ACK with CMD_CODE=201 to dequeue from FCC FIFO
                    var ackBody = RadixXmlBuilder.BuildTransactionAck(UsnCode, token, SharedSecret);
                    await PostXmlAsync(
                        TransactionPortUrl,
                        ackBody,
                        RadixXmlBuilder.OperationTransaction,
                        ct).ConfigureAwait(false);

                    break;
                }
                case RespCodeFifoEmpty:
                    // FIFO empty — no more transactions
                    _logger.LogDebug("FetchTransactionsPull: FIFO empty after {Count} transactions", records.Count);
                    return new TransactionBatch(records, null, false);

                case RespCodeSignatureError:
                    _logger.LogWarning("FetchTransactionsPull: signature error (RESP_CODE=251) — check sharedSecret");
                    return new TransactionBatch(records, null, false);

                default:
                    _logger.LogWarning("FetchTransactionsPull: unexpected RESP_CODE={RespCode}: {RespMsg}",
                        txnResp.RespCode, txnResp.RespMsg);
                    return new TransactionBatch(records, null, false);
            }
        }

        // Reached limit — there may be more transactions
        return new TransactionBatch(records, null, true);
    }

    // =====================================================================
    // IFccAdapter — NormalizeAsync (RX-3.4)
    // =====================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// Normalization rules:
    /// <list type="bullet">
    ///   <item>Dedup key: {FDC_NUM}-{FDC_SAVE_NUM}</item>
    ///   <item>Volume: litres x 1,000,000 via decimal -> long microlitres (NO float)</item>
    ///   <item>Amount: decimal x currency factor -> long minor units (NO float)</item>
    ///   <item>Timestamps: FDC local -> UTC via config timezone</item>
    ///   <item>Pump: resolve PUMP_ADDR/FP via fccPumpAddressMap</item>
    ///   <item>Product code mapping from config</item>
    ///   <item>EFD_ID -> FiscalReceiptNumber</item>
    ///   <item>TOKEN correlation for pre-auth matching</item>
    /// </list>
    /// </remarks>
    public Task<CanonicalTransaction> NormalizeAsync(RawPayloadEnvelope rawPayload, CancellationToken ct)
    {
        var xml = rawPayload.RawJson;
        var txnResp = RadixXmlParser.ParseTransactionResponse(xml);

        if (txnResp is null)
        {
            throw new InvalidOperationException("Failed to parse Radix XML response");
        }

        var trn = txnResp.Transaction
            ?? throw new InvalidOperationException(
                $"No <TRN> element in response (RESP_CODE={txnResp.RespCode})");

        var canonical = NormalizeTransaction(trn, txnResp, rawPayload);
        return Task.FromResult(canonical);
    }

    /// <summary>
    /// Core normalization logic for a parsed TRN element.
    /// All monetary and volume conversions use decimal to avoid floating-point errors.
    /// </summary>
    private CanonicalTransaction NormalizeTransaction(
        RadixTransactionData trn,
        RadixTransactionResponse resp,
        RawPayloadEnvelope rawPayload)
    {
        // --- Dedup key: {FDC_NUM}-{FDC_SAVE_NUM} ---
        if (string.IsNullOrWhiteSpace(trn.FdcNum) || string.IsNullOrWhiteSpace(trn.FdcSaveNum))
        {
            throw new InvalidOperationException(
                $"FDC_NUM and FDC_SAVE_NUM are required for dedup key (FDC_NUM='{trn.FdcNum}', FDC_SAVE_NUM='{trn.FdcSaveNum}')");
        }
        var fccTransactionId = $"{trn.FdcNum}-{trn.FdcSaveNum}";

        // --- Volume: litres -> microlitres via decimal (NO float) ---
        if (string.IsNullOrWhiteSpace(trn.Vol))
        {
            throw new InvalidOperationException("VOL (volume) is required");
        }
        if (!decimal.TryParse(trn.Vol, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var volLitres))
        {
            throw new InvalidOperationException($"Invalid volume value: '{trn.Vol}'");
        }
        var volumeMicrolitres = (long)Math.Round(volLitres * MicrolitresPerLitre, 0, MidpointRounding.AwayFromZero);

        // --- Amount: decimal string -> minor currency units via decimal (NO float) ---
        if (string.IsNullOrWhiteSpace(trn.Amo))
        {
            throw new InvalidOperationException("AMO (amount) is required");
        }
        if (!decimal.TryParse(trn.Amo, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var amoDecimal))
        {
            throw new InvalidOperationException($"Invalid amount value: '{trn.Amo}'");
        }
        var amountMinorUnits = (long)Math.Round(amoDecimal * CurrencyDecimalFactor, 0, MidpointRounding.AwayFromZero);

        if (volumeMicrolitres < 0)
            throw new InvalidOperationException($"Volume must not be negative: '{trn.Vol}'");

        if (amountMinorUnits < 0)
            throw new InvalidOperationException($"Amount must not be negative: '{trn.Amo}'");

        // --- Unit price: decimal string -> minor units per litre via decimal ---
        long unitPriceMinorPerLitre = 0;
        if (!string.IsNullOrWhiteSpace(trn.Price))
        {
            if (decimal.TryParse(trn.Price, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var priceDecimal))
            {
                unitPriceMinorPerLitre = (long)Math.Round(
                    priceDecimal * CurrencyDecimalFactor, 0, MidpointRounding.AwayFromZero);
            }
            else
            {
                throw new InvalidOperationException($"Invalid price value: '{trn.Price}'");
            }
        }

        // --- Timestamps: FDC local -> UTC ---
        var (startedAt, completedAt) = ConvertTimestamps(trn.FdcDate, trn.FdcTime);

        // --- Pump number: resolve from PUMP_ADDR/FP via address map ---
        var pumpNumber = ResolvePumpNumber(trn.PumpAddr, trn.Fp);

        // --- Nozzle number ---
        _ = int.TryParse(trn.Noz, out var nozzleNumber);

        // --- Product code mapping ---
        var rawProductCode = string.IsNullOrWhiteSpace(trn.FdcProd) ? trn.RdgProd : trn.FdcProd;
        var productCode = "UNKNOWN";
        if (_productCodeMapping is not null && !string.IsNullOrWhiteSpace(rawProductCode) &&
            _productCodeMapping.TryGetValue(rawProductCode, out var mappedCode))
        {
            productCode = mappedCode;
        }
        else if (!string.IsNullOrWhiteSpace(rawProductCode))
        {
            productCode = rawProductCode;
        }

        // --- Fiscal receipt number ---
        var fiscalReceiptNumber = string.IsNullOrWhiteSpace(trn.EfdId) ? null : trn.EfdId;

        // --- TOKEN correlation (pre-auth -> transaction matching) ---
        _ = int.TryParse(resp.Token?.Trim(), out var responseToken);
        ActivePreAuth? preAuthEntry = null;
        if (responseToken != 0)
        {
            _activePreAuths.TryRemove(responseToken, out preAuthEntry);
        }

        var correlationId = preAuthEntry is not null
            ? $"RADIX-TOKEN-{preAuthEntry.Token}"
            : Guid.NewGuid().ToString();

        var now = DateTimeOffset.UtcNow;

        return new CanonicalTransaction
        {
            Id = Guid.NewGuid().ToString(),
            FccTransactionId = fccTransactionId,
            SiteCode = rawPayload.SiteCode,
            PumpNumber = pumpNumber,
            NozzleNumber = nozzleNumber,
            ProductCode = productCode,
            VolumeMicrolitres = volumeMicrolitres,
            AmountMinorUnits = amountMinorUnits,
            UnitPriceMinorPerLitre = unitPriceMinorPerLitre,
            CurrencyCode = _currencyCode,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            FiscalReceiptNumber = fiscalReceiptNumber,
            FccVendor = Vendor,
            LegalEntityId = _legalEntityId,
            Status = TransactionStatus.Pending,
            IngestionSource = IngestionSource.EdgeUpload.ToString(),
            IngestedAt = now,
            UpdatedAt = now,
            SchemaVersion = "1.0",
            IsDuplicate = false,
            CorrelationId = correlationId,
            RawPayloadJson = rawPayload.RawJson,
            PreAuthId = preAuthEntry?.PreAuthId,
        };
    }

    // =====================================================================
    // IFccAdapter — SendPreAuthAsync (RX-4.2)
    // =====================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// Pre-auth flow:
    /// 1. Resolve pump -> (PUMP_ADDR, FP) from FccPumpAddressMap
    /// 2. Generate TOKEN, track in ConcurrentDictionary
    /// 3. Build AUTH_DATA XML, POST to auth port P
    /// 4. Parse ACKCODE: 0=Accepted, 251/255/256/258/260=various failures
    /// </remarks>
    public async Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PreAuthTimeout);

            // Step 1: Resolve pump address
            if (!_pumpAddressMap.Value.TryGetValue(command.FccPumpNumber, out var pumpEntry))
            {
                return new PreAuthResult(
                    Accepted: false,
                    FccCorrelationId: null,
                    FccAuthorizationCode: null,
                    ErrorCode: "PUMP_NOT_FOUND",
                    ErrorMessage: $"Pump {command.FccPumpNumber} not found in fccPumpAddressMap");
            }

            // Step 2: Generate TOKEN and track pre-auth
            var token = NextToken();
            var preAuth = new ActivePreAuth(
                Token: token,
                PumpNumber: command.FccPumpNumber,
                PreAuthId: command.PreAuthId,
                CreatedAt: DateTimeOffset.UtcNow);

            _activePreAuths[token] = preAuth;

            // Step 3: Build AUTH_DATA XML
            var presetAmount = ((decimal)command.RequestedAmountMinorUnits / CurrencyDecimalFactor)
                .ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

            var authParams = new RadixPreAuthParams(
                Pump: pumpEntry.PumpAddr,
                Fp: pumpEntry.Fp,
                Authorize: true,
                Product: 0, // 0 = all products
                PresetVolume: "0.00", // Volume preset not used — amount-based
                PresetAmount: presetAmount,
                CustomerName: command.CustomerName,
                CustomerIdType: command.CustomerIdType,
                CustomerId: command.CustomerTaxId,
                MobileNumber: command.CustomerPhone,
                Token: token.ToString());

            var requestBody = RadixXmlBuilder.BuildPreAuthRequest(authParams, SharedSecret);

            // Step 4: POST to auth port
            var responseBody = await PostXmlAsync(
                AuthPortUrl,
                requestBody,
                RadixXmlBuilder.OperationAuthorize,
                cts.Token).ConfigureAwait(false);

            if (responseBody is null)
            {
                _activePreAuths.TryRemove(token, out _);
                return new PreAuthResult(
                    Accepted: false,
                    FccCorrelationId: null,
                    FccAuthorizationCode: null,
                    ErrorCode: "HTTP_ERROR",
                    ErrorMessage: "Failed to send pre-auth request to FCC");
            }

            // Step 5: Parse response and map ACKCODE
            var authResp = RadixXmlParser.ParseAuthResponse(responseBody);
            if (authResp is null)
            {
                _activePreAuths.TryRemove(token, out _);
                return new PreAuthResult(
                    Accepted: false,
                    FccCorrelationId: null,
                    FccAuthorizationCode: null,
                    ErrorCode: "PARSE_ERROR",
                    ErrorMessage: "Failed to parse auth response XML");
            }

            return MapAckCodeToResult(authResp, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Pre-auth timeout (not caller cancellation)
            return new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: "TIMEOUT",
                ErrorMessage: $"Pre-auth request timed out after {PreAuthTimeout.TotalMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            throw; // Preserve caller cancellation
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendPreAuth: {ErrorType}: {Message}", ex.GetType().Name, ex.Message);
            return new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: "ERROR",
                ErrorMessage: $"Pre-auth failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // =====================================================================
    // IFccAdapter — CancelPreAuthAsync (RX-4.2)
    // =====================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// Cancels a pre-auth by sending AUTH_DATA with AUTH=FALSE to the auth port.
    /// Resolves the TOKEN from the fccCorrelationId (format: "RADIX-TOKEN-{token}").
    /// </remarks>
    public async Task<bool> CancelPreAuthAsync(string fccCorrelationId, CancellationToken ct)
    {
        try
        {
            // Parse token from correlation ID: "RADIX-TOKEN-123"
            if (!TryParseTokenFromCorrelationId(fccCorrelationId, out var token))
            {
                _logger.LogWarning("CancelPreAuth: invalid correlation ID format: {CorrelationId}",
                    fccCorrelationId);
                return false;
            }

            // Find the pre-auth entry to get pump address
            if (!_activePreAuths.TryRemove(token, out var preAuth))
            {
                _logger.LogWarning("CancelPreAuth: no active pre-auth for TOKEN={Token}", token);
                return false;
            }

            // Resolve pump address
            if (!_pumpAddressMap.Value.TryGetValue(preAuth.PumpNumber, out var pumpEntry))
            {
                _logger.LogWarning("CancelPreAuth: pump {PumpNumber} not found in address map",
                    preAuth.PumpNumber);
                return false;
            }

            // Build cancel request: AUTH=FALSE
            var cancelBody = RadixXmlBuilder.BuildPreAuthCancelRequest(
                pumpEntry.PumpAddr, pumpEntry.Fp, UsnCode, token, SharedSecret);

            var responseBody = await PostXmlAsync(
                AuthPortUrl,
                cancelBody,
                RadixXmlBuilder.OperationAuthorize,
                ct).ConfigureAwait(false);

            if (responseBody is null)
            {
                _logger.LogWarning("CancelPreAuth: HTTP request failed");
                return false;
            }

            var authResp = RadixXmlParser.ParseAuthResponse(responseBody);
            if (authResp is null)
            {
                _logger.LogWarning("CancelPreAuth: failed to parse response");
                return false;
            }

            var success = authResp.AckCode == AckCodeSuccess;
            if (!success)
            {
                _logger.LogWarning("CancelPreAuth: ACKCODE={AckCode}: {AckMsg}",
                    authResp.AckCode, authResp.AckMsg);
            }

            return success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CancelPreAuth: {ErrorType}: {Message}",
                ex.GetType().Name, ex.Message);
            return false;
        }
    }

    // =====================================================================
    // IFccAdapter — HeartbeatAsync (RX-5.1)
    // =====================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// Liveness probe using CMD_CODE=55 (product/price read) on port P+1.
    /// 5-second hard timeout. Never throws — returns false on any error.
    /// Signature errors (RESP_CODE=251) are logged as warnings (config issue).
    /// </remarks>
    public async Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HeartbeatTimeout);

            var token = NextToken();
            var requestBody = RadixXmlBuilder.BuildProductReadRequest(UsnCode, token, SharedSecret);

            var responseBody = await PostXmlAsync(
                TransactionPortUrl,
                requestBody,
                RadixXmlBuilder.OperationProducts,
                cts.Token).ConfigureAwait(false);

            if (responseBody is null)
            {
                _logger.LogDebug("Heartbeat: no response from FCC");
                return false;
            }

            var productResp = RadixXmlParser.ParseProductResponse(responseBody);
            if (productResp is null)
            {
                _logger.LogWarning("Heartbeat: failed to parse response");
                return false;
            }

            if (productResp.RespCode == RespCodeSignatureError)
            {
                _logger.LogWarning("Heartbeat: signature error (RESP_CODE=251) — check sharedSecret configuration");
                return false;
            }

            return productResp.RespCode == RespCodeSuccess;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Heartbeat timeout (not caller cancellation)
            _logger.LogDebug("Heartbeat: timeout after {TimeoutMs}ms", HeartbeatTimeout.TotalMilliseconds);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw; // Preserve caller cancellation
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Heartbeat: {ErrorType}: {Message}", ex.GetType().Name, ex.Message);
            ResetModeState();
            return false;
        }
    }

    // =====================================================================
    // IFccAdapter — GetPumpStatusAsync
    // =====================================================================

    /// <inheritdoc/>
    /// <remarks>Radix does not expose real-time pump status. Always returns empty list.</remarks>
    public Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<PumpStatus>>([]);
    }

    // =====================================================================
    // IFccAdapter — AcknowledgeTransactionsAsync
    // =====================================================================

    /// <inheritdoc/>
    /// <remarks>
    /// No-op — Radix ACK (CMD_CODE=201) is sent inline during the fetch loop.
    /// There is no separate acknowledgment step required by the caller.
    /// </remarks>
    public Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct)
    {
        return Task.FromResult(true);
    }

    // =====================================================================
    // Private — Mode management (RX-5.1)
    // =====================================================================

    /// <summary>
    /// Sends CMD_CODE=20 to set the transaction transfer mode, but only if
    /// the cached mode differs from the requested mode.
    /// </summary>
    private async Task<bool> EnsureModeAsync(int mode, CancellationToken ct)
    {
        if (_currentMode == mode) return true;

        try
        {
            var token = NextToken();
            var requestBody = RadixXmlBuilder.BuildModeChangeRequest(UsnCode, mode, token, SharedSecret);

            var responseBody = await PostXmlAsync(
                TransactionPortUrl,
                requestBody,
                RadixXmlBuilder.OperationTransaction,
                ct).ConfigureAwait(false);

            if (responseBody is null)
            {
                _logger.LogWarning("EnsureMode: HTTP request failed");
                return false;
            }

            var parseResult = RadixXmlParser.ParseTransactionResponse(responseBody);
            if (parseResult is null)
            {
                _logger.LogWarning("EnsureMode: failed to parse response");
                return false;
            }

            if (parseResult.RespCode == RespCodeSuccess)
            {
                _currentMode = mode;
                _logger.LogDebug("Mode changed to {Mode}", mode);
                return true;
            }

            _logger.LogWarning("EnsureMode: RESP_CODE={RespCode}", parseResult.RespCode);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnsureMode: {ErrorType}: {Message}", ex.GetType().Name, ex.Message);
            ResetModeState();
            return false;
        }
    }

    /// <summary>Reset cached mode on connectivity loss.</summary>
    private void ResetModeState()
    {
        _currentMode = ModeUnknown;
    }

    // =====================================================================
    // Push listener lifecycle (RX-5.1)
    // =====================================================================

    /// <summary>
    /// Starts the push listener and sets the FDC to UNSOLICITED mode.
    /// </summary>
    /// <returns>true if both the listener started and the mode was set successfully.</returns>
    private async Task<bool> EnsurePushListenerRunningAsync(CancellationToken ct)
    {
        // Create listener if not yet initialized
        _pushListener ??= new RadixPushListener(
            listenPort: PushListenerPort,
            expectedUsnCode: UsnCode,
            sharedSecret: SharedSecret,
            logger: _logger);

        // Start the HTTP listener if not already running
        if (!_pushListener.IsRunning)
        {
            if (!await _pushListener.StartAsync().ConfigureAwait(false))
            {
                _logger.LogWarning("Failed to start push listener on port {Port}", PushListenerPort);
                return false;
            }
        }

        // Set FDC to UNSOLICITED mode
        if (!await EnsureModeAsync(ModeUnsolicited, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Failed to set UNSOLICITED mode on FDC");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stops the push listener and resets mode state.
    /// Called when the adapter is being shut down or switching back to pull mode.
    /// </summary>
    public async Task StopPushListenerAsync()
    {
        if (_pushListener is not null)
        {
            await _pushListener.StopAsync().ConfigureAwait(false);
            _pushListener = null;
        }
        ResetModeState();
    }

    /// <summary>
    /// Collects transactions pushed by the FDC via the <see cref="RadixPushListener"/>.
    /// Drains the listener's queue and wraps each raw XML payload in a
    /// <see cref="RawPayloadEnvelope"/>.
    /// </summary>
    /// <param name="limit">Maximum number of transactions to collect.</param>
    /// <returns>List of raw payload envelopes from pushed payloads.</returns>
    private List<RawPayloadEnvelope> CollectPushedEnvelopes(int limit)
    {
        if (_pushListener is null) return [];

        var pushed = _pushListener.DrainQueue(limit);
        if (pushed.Count == 0) return [];

        var envelopes = new List<RawPayloadEnvelope>(pushed.Count);
        foreach (var item in pushed)
        {
            envelopes.Add(new RawPayloadEnvelope(
                FccVendor: Vendor,
                SiteCode: _siteCode,
                RawJson: item.RawXml,
                ReceivedAt: item.ReceivedAt));
        }

        return envelopes;
    }

    // =====================================================================
    // Private — Token counter
    // =====================================================================

    /// <summary>Generates the next sequential token (1-65535), wrapping around and skipping 0. Thread-safe.</summary>
    private int NextToken()
    {
        // Interlocked compare-and-swap loop for thread-safe wrap-around
        while (true)
        {
            var current = Volatile.Read(ref _tokenCounter);
            var next = (current + 1) % TokenWrap;
            if (next == 0) next = 1; // Skip 0 — TOKEN=0 means "Normal Order" (no pre-auth)
            if (Interlocked.CompareExchange(ref _tokenCounter, next, current) == current)
                return current;
        }
    }

    // =====================================================================
    // Private — HTTP POST helper
    // =====================================================================

    /// <summary>
    /// Sends an HTTP POST with XML body and Radix headers.
    /// Returns the response body string, or null on failure.
    /// </summary>
    private async Task<string?> PostXmlAsync(
        string url,
        string xmlBody,
        string operation,
        CancellationToken ct)
    {
        try
        {
            var client = _httpFactory.CreateClient("RadixFcc");
            var headers = RadixXmlBuilder.BuildHttpHeaders(UsnCode, operation);

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(xmlBody, Encoding.UTF8, "Application/xml");

            foreach (var (key, value) in headers)
            {
                // Content-Type is set via StringContent; skip to avoid duplicate header
                if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;

                request.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("HTTP POST to {Url} returned {StatusCode}", url, response.StatusCode);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP POST to {Url}: {ErrorType}: {Message}",
                url, ex.GetType().Name, ex.Message);
            return null;
        }
    }

    // =====================================================================
    // Private — ACKCODE mapping
    // =====================================================================

    /// <summary>Maps Radix ACKCODE to canonical PreAuthResult.</summary>
    private PreAuthResult MapAckCodeToResult(RadixAuthResponse authResp, int token)
    {
        return authResp.AckCode switch
        {
            AckCodeSuccess => new PreAuthResult(
                Accepted: true,
                FccCorrelationId: $"RADIX-TOKEN-{token}",
                FccAuthorizationCode: $"RADIX-TOKEN-{token}",
                ErrorCode: null,
                ErrorMessage: null),

            AckCodeSignatureError => RemovePreAuthAndReturn(token, new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: "SIGNATURE_ERROR",
                ErrorMessage: "Signature error (ACKCODE=251) — check sharedSecret configuration")),

            AckCodeBadXml => RemovePreAuthAndReturn(token, new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: "BAD_XML",
                ErrorMessage: $"Nozzle not lifted or bad XML format (ACKCODE=255): {authResp.AckMsg}")),

            AckCodeBadHeader => new PreAuthResult(
                Accepted: false,
                FccCorrelationId: $"RADIX-TOKEN-{token}",
                FccAuthorizationCode: null,
                ErrorCode: "ALREADY_AUTHORIZED",
                ErrorMessage: $"Already authorized or bad header (ACKCODE=256): {authResp.AckMsg}"),

            AckCodePumpNotReady => RemovePreAuthAndReturn(token, new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: "PUMP_NOT_READY",
                ErrorMessage: $"Pump not ready or max exceeded (ACKCODE=258): {authResp.AckMsg}")),

            AckCodeDsbOffline => RemovePreAuthAndReturn(token, new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: "DSB_OFFLINE",
                ErrorMessage: $"DSB offline or system error (ACKCODE=260): {authResp.AckMsg}")),

            _ => RemovePreAuthAndReturn(token, new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: "UNKNOWN",
                ErrorMessage: $"Unknown ACKCODE={authResp.AckCode}: {authResp.AckMsg}")),
        };
    }

    /// <summary>Removes the pre-auth entry and returns the given result.</summary>
    private PreAuthResult RemovePreAuthAndReturn(int token, PreAuthResult result)
    {
        _activePreAuths.TryRemove(token, out _);
        return result;
    }

    // =====================================================================
    // Private — Timestamp conversion
    // =====================================================================

    /// <summary>
    /// Converts FDC local date + time strings to UTC DateTimeOffset values.
    /// For Radix, startedAt == completedAt (single timestamp per transaction).
    /// </summary>
    private (DateTimeOffset StartedAt, DateTimeOffset CompletedAt) ConvertTimestamps(
        string fdcDate, string fdcTime)
    {
        if (string.IsNullOrWhiteSpace(fdcDate) || string.IsNullOrWhiteSpace(fdcTime))
        {
            var now = DateTimeOffset.UtcNow;
            return (now, now);
        }

        try
        {
            var localStr = $"{fdcDate}T{fdcTime}";
            var localDateTime = DateTime.Parse(localStr, System.Globalization.CultureInfo.InvariantCulture);

            var tz = ResolveTimeZone(_timezone);
            var utcOffset = tz.GetUtcOffset(localDateTime);
            var dto = new DateTimeOffset(localDateTime, utcOffset);
            var utcDto = dto.ToUniversalTime();

            return (utcDto, utcDto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Timestamp conversion failed for FDC_DATE='{Date}', FDC_TIME='{Time}': {Message}",
                fdcDate, fdcTime, ex.Message);
            var now = DateTimeOffset.UtcNow;
            return (now, now);
        }
    }

    /// <summary>
    /// Resolves a TimeZoneInfo from an IANA or Windows timezone ID.
    /// On Windows without ICU, FindSystemTimeZoneById may not recognize IANA IDs
    /// (e.g. "Africa/Dar_es_Salaam"). This method falls back to converting the
    /// IANA ID to a Windows ID via TryConvertIanaIdToWindowsId.
    /// </summary>
    private static TimeZoneInfo ResolveTimeZone(string timezoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            // IANA ID not recognized — try converting to Windows ID
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(timezoneId, out var windowsId))
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            throw;
        }
    }

    // =====================================================================
    // Private — Pump address resolution
    // =====================================================================

    /// <summary>
    /// Resolves a canonical pump number from FCC-native PUMP_ADDR and FP values.
    /// Uses the reverse pump address map. Falls back to PUMP_ADDR + pumpNumberOffset.
    /// </summary>
    private int ResolvePumpNumber(string pumpAddr, string fp)
    {
        var addrStr = string.IsNullOrWhiteSpace(pumpAddr) ? "0" : pumpAddr;
        var fpStr = string.IsNullOrWhiteSpace(fp) ? "0" : fp;
        var key = $"{addrStr}-{fpStr}";

        if (_reversePumpAddressMap.Value.TryGetValue(key, out var pumpNumber))
            return pumpNumber;

        // Fallback: PUMP_ADDR + offset
        _ = int.TryParse(addrStr, out var addrInt);
        return addrInt + _pumpNumberOffset;
    }

    // =====================================================================
    // Private — Pump address map parsing
    // =====================================================================

    /// <summary>
    /// Parses the fccPumpAddressMap JSON string into a typed dictionary.
    /// Expected JSON format:
    /// { "1": { "pumpAddr": 0, "fp": 0 }, "2": { "pumpAddr": 0, "fp": 1 } }
    /// </summary>
    private Dictionary<int, (int PumpAddr, int Fp)> ParsePumpAddressMap(string? json)
    {
        var result = new Dictionary<int, (int PumpAddr, int Fp)>();

        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out var pumpNumber))
                    continue;

                var obj = prop.Value;
                var pumpAddr = obj.TryGetProperty("pumpAddr", out var pa) ? pa.GetInt32() : 0;
                var fpVal = obj.TryGetProperty("fp", out var fpElem) ? fpElem.GetInt32() : 0;

                result[pumpNumber] = (pumpAddr, fpVal);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse fccPumpAddressMap: {Message}", ex.Message);
        }

        return result;
    }

    // =====================================================================
    // Private — Correlation ID parsing
    // =====================================================================

    /// <summary>
    /// Parses a TOKEN value from a correlation ID string (format: "RADIX-TOKEN-{token}").
    /// </summary>
    private static bool TryParseTokenFromCorrelationId(string correlationId, out int token)
    {
        token = 0;
        if (string.IsNullOrWhiteSpace(correlationId))
            return false;

        const string prefix = "RADIX-TOKEN-";
        if (!correlationId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(correlationId.AsSpan(prefix.Length), out token);
    }

    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary>FCC vendor identifier.</summary>
    public const string Vendor = "RADIX";

    /// <summary>Adapter version.</summary>
    public const string AdapterVersion = "1.0.0";

    /// <summary>Protocol identifier.</summary>
    public const string Protocol = "HTTP_XML";

    /// <summary>Returns true if this adapter has a working implementation.</summary>
    public const bool IsImplemented = true;

    /// <summary>Hard timeout for heartbeat probe (5 seconds).</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Hard timeout for pre-auth requests (10 seconds).</summary>
    public static readonly TimeSpan PreAuthTimeout = TimeSpan.FromSeconds(10);

    /// <summary>TTL for pre-auth entries: entries older than this are purged to prevent memory leaks.</summary>
    public static readonly TimeSpan PreAuthTtl = TimeSpan.FromMinutes(30);

    /// <summary>Maximum number of transactions to fetch in a single batch.</summary>
    public const int MaxFetchLimit = 200;

    /// <summary>Token counter wrap boundary: 0-65535.</summary>
    public const int TokenWrap = 65536;

    /// <summary>Successful response code.</summary>
    public const int RespCodeSuccess = 201;

    /// <summary>FIFO buffer empty — no more transactions.</summary>
    public const int RespCodeFifoEmpty = 205;

    /// <summary>Signature error response code — indicates misconfigured shared secret.</summary>
    public const int RespCodeSignatureError = 251;

    // --- ACKCODE values (auth responses) ---

    /// <summary>Pre-auth success.</summary>
    public const int AckCodeSuccess = 0;

    /// <summary>Signature error — check shared secret.</summary>
    public const int AckCodeSignatureError = 251;

    /// <summary>Bad XML format / nozzle not lifted.</summary>
    public const int AckCodeBadXml = 255;

    /// <summary>Bad header format / already authorized.</summary>
    public const int AckCodeBadHeader = 256;

    /// <summary>Pump not ready / max exceeded.</summary>
    public const int AckCodePumpNotReady = 258;

    /// <summary>DSB offline / system error.</summary>
    public const int AckCodeDsbOffline = 260;

    // --- Mode constants ---

    /// <summary>Mode not yet known or reset after connectivity loss.</summary>
    public const int ModeUnknown = -1;

    /// <summary>ON_DEMAND (pull) mode — host requests transactions. Radix protocol MODE=1.</summary>
    public const int ModeOnDemand = 1;

    /// <summary>UNSOLICITED (push) mode — FDC posts transactions to the listener.</summary>
    public const int ModeUnsolicited = 2;

    /// <summary>RESP_CODE for unsolicited push transactions from the FDC.</summary>
    public const int RespCodeUnsolicited = 30;
}

// ---------------------------------------------------------------------------
// Supporting data classes
// ---------------------------------------------------------------------------

/// <summary>
/// Tracks an active pre-authorization for later TOKEN correlation with
/// the resulting dispense transaction.
///
/// Stored in RadixAdapter._activePreAuths keyed by TOKEN value.
/// </summary>
public sealed record ActivePreAuth(
    /// <summary>Radix TOKEN value (0-65535) assigned to this pre-auth.</summary>
    int Token,
    /// <summary>Canonical pump number from the original PreAuthCommand.</summary>
    int PumpNumber,
    /// <summary>Pre-auth ID for correlation, if provided.</summary>
    string? PreAuthId,
    /// <summary>When this pre-auth was created.</summary>
    DateTimeOffset CreatedAt);
