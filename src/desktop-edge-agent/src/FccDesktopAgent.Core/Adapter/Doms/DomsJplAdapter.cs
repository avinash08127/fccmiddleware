using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms.Jpl;
using FccDesktopAgent.Core.Adapter.Doms.Mapping;
using FccDesktopAgent.Core.Adapter.Doms.Model;
using FccDesktopAgent.Core.Adapter.Doms.Protocol;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Doms;

/// <summary>
/// DomsJplAdapter -- Full DOMS TCP/JPL adapter implementation.
///
/// Implements both <see cref="IFccAdapter"/> (business operations) and
/// <see cref="IFccConnectionLifecycle"/> (persistent TCP connection management).
///
/// Protocol: binary STX/ETX-framed JSON over persistent TCP socket.
/// Auth: FcLogon handshake with access code.
/// Heartbeat: empty frame every N seconds (default 30).
/// Fetch: lock -> read -> clear supervised buffer.
/// Pre-auth: authorize_Fp_req / deauthorize_Fp_req JPL messages.
/// </summary>
public sealed class DomsJplAdapter : IFccAdapter, IFccConnectionLifecycle, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly FccConnectionConfig _config;
    private readonly string _siteCode;
    private readonly string _legalEntityId;
    private readonly string _currencyCode;
    private readonly string _timezone;
    private readonly int _pumpNumberOffset;
    private readonly IReadOnlyDictionary<string, string>? _productCodeMapping;
    private readonly ILogger<DomsJplAdapter> _logger;
    private readonly JplTcpClient _tcpClient;
    private readonly JplHeartbeatManager _heartbeatManager;

    private IFccEventListener? _eventListener;
    private volatile bool _logonComplete;
    private bool _disposed;

    public DomsJplAdapter(
        FccConnectionConfig config,
        string siteCode,
        string legalEntityId,
        string currencyCode,
        string timezone,
        int pumpNumberOffset,
        IReadOnlyDictionary<string, string>? productCodeMapping,
        ILogger<DomsJplAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _siteCode = siteCode;
        _legalEntityId = legalEntityId;
        _currencyCode = currencyCode;
        _timezone = timezone;
        _pumpNumberOffset = pumpNumberOffset;
        _productCodeMapping = productCodeMapping;
        _logger = logger;

        var host = new Uri(config.BaseUrl).Host;
        var port = config.JplPort ?? config.AuthPort ?? 4711;

        _tcpClient = new JplTcpClient(
            host: host,
            port: port,
            logger: logger,
            requestTimeout: config.RequestTimeout);

        _heartbeatManager = new JplHeartbeatManager(
            client: _tcpClient,
            intervalSeconds: config.HeartbeatIntervalSeconds ?? 30,
            logger: logger);
    }

    // -- IFccConnectionLifecycle -------------------------------------------------

    /// <inheritdoc/>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _tcpClient.OnDisconnected += OnTcpDisconnected;
        _tcpClient.OnFrameReceived += OnUnsolicitedMessage;
        _heartbeatManager.OnDeadConnection += OnDeadConnection;

        // Connect TCP
        await _tcpClient.ConnectAsync(ct);

        // FcLogon handshake
        var logonRequest = DomsLogonHandler.BuildLogonRequest(
            fcAccessCode: _config.FcAccessCode ?? string.Empty,
            posVersionId: _config.PosVersionId ?? "FccMiddleware/1.0",
            countryCode: _config.DomsCountryCode ?? "ZA");

        var logonResponse = await _tcpClient.SendAsync(logonRequest, DomsLogonHandler.LogonResponse, ct);
        DomsLogonHandler.ValidateLogonResponse(logonResponse);
        _logonComplete = true;

        _logger.LogInformation("DOMS FcLogon successful");

        // Start heartbeat
        await _heartbeatManager.StartAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _heartbeatManager.StopAsync(ct);
        _logonComplete = false;

        _tcpClient.OnDisconnected -= OnTcpDisconnected;
        _tcpClient.OnFrameReceived -= OnUnsolicitedMessage;
        _heartbeatManager.OnDeadConnection -= OnDeadConnection;

        await _tcpClient.DisconnectAsync(ct);
    }

    /// <inheritdoc/>
    public bool IsConnected => _tcpClient.IsConnected && _logonComplete;

    /// <inheritdoc/>
    public void SetEventListener(IFccEventListener? listener)
    {
        _eventListener = listener;
    }

    // -- IFccAdapter -------------------------------------------------------------

    /// <inheritdoc/>
    public Task<CanonicalTransaction> NormalizeAsync(RawPayloadEnvelope rawPayload, CancellationToken ct)
    {
        var dto = JsonSerializer.Deserialize<DomsJplTransactionDto>(rawPayload.RawJson, JsonOptions)
            ?? throw new FccAdapterException(
                "DOMS JPL normalization: null result from deserialization", isRecoverable: false);

        var canonical = DomsCanonicalMapper.MapToCanonical(
            dto, rawPayload.SiteCode, _legalEntityId, _currencyCode,
            _timezone, _pumpNumberOffset, _productCodeMapping)
            ?? throw new FccAdapterException(
                "DOMS JPL normalization: mapping returned null", isRecoverable: false);

        return Task.FromResult(canonical);
    }

    /// <inheritdoc/>
    public async Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct)
    {
        if (!IsConnected)
        {
            return new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: "NOT_CONNECTED",
                ErrorMessage: "DOMS TCP not connected");
        }

        try
        {
            var fpId = command.FccPumpNumber - _pumpNumberOffset;

            var authRequest = DomsPreAuthHandler.BuildAuthRequest(
                fpId: fpId,
                nozzleId: command.FccNozzleNumber,
                amountMinorUnits: command.RequestedAmountMinorUnits,
                currencyCode: command.Currency);

            var response = await _tcpClient.SendAsync(authRequest, DomsPreAuthHandler.AuthResponse, ct);
            var result = DomsPreAuthHandler.ParseAuthResponse(response);

            return new PreAuthResult(
                Accepted: result.Accepted,
                FccCorrelationId: result.CorrelationId,
                FccAuthorizationCode: result.AuthorizationCode,
                ErrorCode: result.Accepted ? null : "AUTH_DECLINED",
                ErrorMessage: result.Message,
                ExpiresAt: !string.IsNullOrEmpty(result.ExpiresAtUtc) &&
                           DateTimeOffset.TryParse(result.ExpiresAtUtc, out var exp)
                    ? exp
                    : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DOMS pre-auth failed for pump {Pump}", command.FccPumpNumber);
            return new PreAuthResult(
                Accepted: false,
                FccCorrelationId: null,
                FccAuthorizationCode: null,
                ErrorCode: "TRANSPORT_ERROR",
                ErrorMessage: $"DOMS pre-auth failed: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct)
    {
        if (!IsConnected)
            return [];

        try
        {
            var request = DomsPumpStatusParser.BuildStatusRequest(fpId: 0);
            var response = await _tcpClient.SendAsync(request, DomsPumpStatusParser.StatusResponse, ct);

            return DomsPumpStatusParser.ParseStatusResponse(
                response: response,
                siteCode: _siteCode,
                currencyCode: _currencyCode,
                pumpNumberOffset: _pumpNumberOffset,
                observedAtUtc: DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DOMS getPumpStatus failed");
            return [];
        }
    }

    /// <inheritdoc/>
    public Task<bool> HeartbeatAsync(CancellationToken ct)
    {
        return Task.FromResult(_tcpClient.IsConnected && _logonComplete);
    }

    /// <inheritdoc/>
    public async Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct)
    {
        if (!IsConnected)
            return new TransactionBatch(Records: [], NextCursor: null, HasMore: false);

        try
        {
            // Step 1: Lock the supervised buffer
            var lockRequest = DomsTransactionParser.BuildLockRequest();
            var lockResponse = await _tcpClient.SendAsync(lockRequest, DomsTransactionParser.LockResponse, ct);

            if (!DomsTransactionParser.ValidateLockResponse(lockResponse))
                return new TransactionBatch(Records: [], NextCursor: null, HasMore: false);

            // Step 2: Read transactions
            var readRequest = DomsTransactionParser.BuildReadRequest();
            var readResponse = await _tcpClient.SendAsync(readRequest, DomsTransactionParser.ReadResponse, ct);

            var domsTxns = DomsTransactionParser.ParseReadResponse(readResponse);

            if (domsTxns.Count == 0)
                return new TransactionBatch(Records: [], NextCursor: null, HasMore: false);

            // Step 3: Map to raw payload envelopes wrapping the DTO JSON
            var now = DateTimeOffset.UtcNow;
            var records = new List<RawPayloadEnvelope>(domsTxns.Count);
            foreach (var dto in domsTxns)
            {
                var rawJson = JsonSerializer.Serialize(dto, JsonOptions);
                records.Add(new RawPayloadEnvelope(
                    FccVendor: "DOMS",
                    SiteCode: _siteCode,
                    RawJson: rawJson,
                    ReceivedAt: now));
            }

            // Step 4: Clear the buffer
            var clearRequest = DomsTransactionParser.BuildClearRequest(count: domsTxns.Count);
            var clearResponse = await _tcpClient.SendAsync(clearRequest, DomsTransactionParser.ClearResponse, ct);

            if (!DomsTransactionParser.ValidateClearResponse(clearResponse))
            {
                _logger.LogWarning("DOMS buffer clear failed -- transactions may be re-fetched");
            }

            return new TransactionBatch(
                Records: records,
                NextCursor: null,
                HasMore: false); // DOMS buffer is drained in one pass
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "DOMS fetchTransactions failed");
            return new TransactionBatch(Records: [], NextCursor: null, HasMore: false);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CancelPreAuthAsync(string fccCorrelationId, CancellationToken ct)
    {
        if (!IsConnected)
            return false;

        try
        {
            // Attempt to parse the correlationId as FpId for deauth.
            // The correlationId for DOMS TCP is the FpId used during authorization.
            if (!int.TryParse(fccCorrelationId, out var fpId))
            {
                _logger.LogWarning(
                    "DOMS CancelPreAuth: cannot parse correlationId '{CorrelationId}' as FpId",
                    fccCorrelationId);
                return false;
            }

            var deauthRequest = DomsPreAuthHandler.BuildDeauthRequest(fpId);
            await _tcpClient.SendAsync(deauthRequest, "deauthorize_Fp_resp", ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DOMS CancelPreAuth failed for {CorrelationId}", fccCorrelationId);
            return false;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// No-op -- DOMS acknowledgment is implicit in the lock-read-clear sequence.
    /// Clear was already sent during FetchTransactionsAsync.
    /// </remarks>
    public Task<bool> AcknowledgeTransactionsAsync(IReadOnlyList<string> transactionIds, CancellationToken ct)
        => Task.FromResult(true);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync();
        await _heartbeatManager.DisposeAsync();
        await _tcpClient.DisposeAsync();
    }

    // -- Unsolicited message handling --------------------------------------------

    private void OnUnsolicitedMessage(JplMessage message)
    {
        _logger.LogDebug("DOMS unsolicited message: {Name}", message.Name);

        var data = message.Data;
        if (data is null) return;

        switch (message.Name)
        {
            case "FpStatusChanged":
            {
                if (!data.TryGetValue("FpId", out var fpIdStr) || !int.TryParse(fpIdStr, out var fpId))
                    return;
                if (!data.TryGetValue("FpMainState", out var stateStr) || !int.TryParse(stateStr, out var stateCode))
                    return;

                var canonicalState = Enum.IsDefined(typeof(DomsFpMainState), stateCode)
                    ? ((DomsFpMainState)stateCode).ToCanonicalPumpState()
                    : PumpState.Unknown;
                var pumpNumber = fpId + _pumpNumberOffset;

                _eventListener?.OnPumpStatusChanged(pumpNumber, canonicalState, stateCode.ToString());
                break;
            }

            case "TransactionAvailable":
            {
                if (!data.TryGetValue("FpId", out var fpIdStr) || !int.TryParse(fpIdStr, out var fpId))
                    return;

                var bufferIndex = data.TryGetValue("BufferIndex", out var biStr) && int.TryParse(biStr, out var bi)
                    ? (int?)bi
                    : null;

                _eventListener?.OnTransactionAvailable(new TransactionNotification(
                    FpId: fpId + _pumpNumberOffset,
                    TransactionBufferIndex: bufferIndex,
                    Timestamp: DateTimeOffset.UtcNow));
                break;
            }

            case "FuellingUpdate":
            {
                if (!data.TryGetValue("FpId", out var fpIdStr) || !int.TryParse(fpIdStr, out var fpId))
                    return;
                if (!data.TryGetValue("Volume", out var volStr) || !long.TryParse(volStr, out var volumeCl))
                    return;
                if (!data.TryGetValue("Amount", out var amtStr) || !long.TryParse(amtStr, out var amountX10))
                    return;

                var pumpNumber = fpId + _pumpNumberOffset;

                _eventListener?.OnFuellingUpdate(
                    pumpNumber: pumpNumber,
                    volumeMicrolitres: DomsCanonicalMapper.CentilitresToMicrolitres(volumeCl),
                    amountMinorUnits: DomsCanonicalMapper.DomsAmountToMinorUnits(amountX10));
                break;
            }
        }
    }

    private void OnTcpDisconnected(string reason)
    {
        _logonComplete = false;
        _ = _heartbeatManager.StopAsync();
        _eventListener?.OnConnectionLost(reason);
    }

    private void OnDeadConnection(string reason)
    {
        _logonComplete = false;
        _ = _heartbeatManager.StopAsync();
        _eventListener?.OnConnectionLost($"Dead connection ({reason})");
    }
}
