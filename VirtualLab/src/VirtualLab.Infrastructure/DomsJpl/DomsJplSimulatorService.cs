using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtualLab.Application.PreAuth;

namespace VirtualLab.Infrastructure.DomsJpl;

/// <summary>
/// Hosted service that runs a TCP server simulating the DOMS JPL protocol.
/// Accepts persistent connections (one per POS client), uses binary STX/ETX framing,
/// and responds to the standard JPL message set. Designed for E2E testing of the
/// DOMS TCP/JPL adapter.
/// </summary>
public sealed class DomsJplSimulatorService(
    DomsJplSimulatorState state,
    IOptions<DomsJplSimulatorOptions> options,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<DomsJplSimulatorService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();
    private TcpListener? _listener;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DomsJplSimulatorOptions config = options.Value;
        state.Initialize(config.PumpCount);

        _listener = new TcpListener(IPAddress.Any, config.ListenPort);
        _listener.Start();
        state.IsListening = true;

        logger.LogInformation(
            "DOMS JPL simulator listening on port {Port} with {PumpCount} pumps.",
            config.ListenPort,
            config.PumpCount);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        finally
        {
            _listener.Stop();
            state.IsListening = false;
            logger.LogInformation("DOMS JPL simulator stopped.");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        string remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        state.IncrementConnectedClients();
        logger.LogInformation("DOMS JPL client connected: {Endpoint}", remoteEndpoint);

        try
        {
            await using NetworkStream stream = client.GetStream();
            ClientSession session = new(Guid.NewGuid(), client, stream, remoteEndpoint);
            _sessions[session.Id] = session;
            byte[] readBuffer = new byte[8192];
            int bufferLength = 0;
            bool loggedIn = false;
            DateTimeOffset lastHeartbeat = DateTimeOffset.UtcNow;

            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            while (!linkedCts.Token.IsCancellationRequested && client.Connected)
            {
                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(
                        readBuffer.AsMemory(bufferLength, readBuffer.Length - bufferLength),
                        linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                if (bytesRead == 0)
                {
                    break; // Client disconnected
                }

                bufferLength += bytesRead;

                // Grow buffer if needed
                if (bufferLength >= readBuffer.Length - 1024)
                {
                    Array.Resize(ref readBuffer, readBuffer.Length * 2);
                }

                IReadOnlyList<DecodedFrame> frames = DomsJplFrameCodec.DecodeAll(ref readBuffer, ref bufferLength);

                foreach (DecodedFrame frame in frames)
                {
                    state.IncrementMessagesProcessed();

                    if (frame.IsHeartbeat)
                    {
                        lastHeartbeat = DateTimeOffset.UtcNow;
                        if (!state.ErrorInjection.SuppressHeartbeats || !TryConsumeErrorShot())
                        {
                            await session.SendAsync(DomsJplFrameCodec.EncodeHeartbeat(), linkedCts.Token);
                        }

                        continue;
                    }

                    // Apply configured response delay
                    if (state.ErrorInjection.ResponseDelayMs > 0)
                    {
                        await Task.Delay(state.ErrorInjection.ResponseDelayMs, linkedCts.Token);
                    }

                    (byte[]? response, bool? newLoggedIn) = await ProcessMessageAsync(frame.Payload, loggedIn, linkedCts.Token);
                    if (newLoggedIn.HasValue)
                    {
                        loggedIn = newLoggedIn.Value;
                    }

                    session.IsLoggedIn = loggedIn;
                    if (response is null)
                    {
                        continue;
                    }

                    // Malformed frame injection
                    if (state.ErrorInjection.SendMalformedFrame && TryConsumeErrorShot())
                    {
                        // Send frame without ETX
                        byte[] malformed = new byte[response.Length - 1];
                        Array.Copy(response, malformed, malformed.Length);
                        await session.SendAsync(malformed, linkedCts.Token);
                        continue;
                    }

                    await session.SendAsync(response, linkedCts.Token);

                    // Drop connection after logon if configured
                    if (state.ErrorInjection.DropConnectionAfterLogon && loggedIn && TryConsumeErrorShot())
                    {
                        logger.LogWarning("Error injection: dropping connection after logon for {Endpoint}.", remoteEndpoint);
                        break;
                    }
                }

                // Check heartbeat timeout
                DomsJplSimulatorOptions config = options.Value;
                if (config.HeartbeatTimeoutSeconds > 0 &&
                    (DateTimeOffset.UtcNow - lastHeartbeat).TotalSeconds > config.HeartbeatTimeoutSeconds)
                {
                    logger.LogWarning("Heartbeat timeout for {Endpoint}. Disconnecting.", remoteEndpoint);
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Error handling DOMS JPL client {Endpoint}.", remoteEndpoint);
        }
        finally
        {
            KeyValuePair<Guid, ClientSession> current = _sessions.FirstOrDefault(x => ReferenceEquals(x.Value.Client, client));
            if (current.Value is not null)
            {
                _sessions.TryRemove(current.Key, out _);
            }

            state.DecrementConnectedClients();
            client.Dispose();
            logger.LogInformation("DOMS JPL client disconnected: {Endpoint}", remoteEndpoint);
        }
    }

    /// <summary>
    /// Processes a single JPL message. Returns (response bytes, new loggedIn state if changed).
    /// </summary>
    private async Task<(byte[]? Response, bool? NewLoggedIn)> ProcessMessageAsync(string jsonPayload, bool loggedIn, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonPayload);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("type", out JsonElement typeElement))
            {
                logger.LogWarning("DOMS JPL message missing 'type' field: {Payload}", Truncate(jsonPayload));
                return (null, null);
            }

            string messageType = typeElement.GetString() ?? string.Empty;

            if (messageType == "FcLogon_req")
            {
                bool newLoggedIn = loggedIn;
                byte[] logonResp = HandleFcLogon(root, ref newLoggedIn);
                return (logonResp, newLoggedIn);
            }

            byte[]? response = messageType switch
            {
                "FpStatus_req" => HandleFpStatus(root),
                "FpSupTrans_lock_req" => HandleFpSupTransLock(root),
                "FpSupTrans_read_req" => HandleFpSupTransRead(root),
                "FpSupTrans_clear_req" => HandleFpSupTransClear(root),
                "authorize_Fp_req" => await HandleAuthorizeFpAsync(root, cancellationToken),
                "deauthorize_Fp_req" => await HandleDeauthorizeFpAsync(root, cancellationToken),
                // Phase 7: Pump control messages
                "FpEmergencyStop_req" => HandleFpEmergencyStop(root),
                "FpCancelEmergencyStop_req" => HandleFpCancelEmergencyStop(root),
                "FpClose_req" => HandleFpClose(root),
                "FpOpen_req" => HandleFpOpen(root),
                // Phase 7: Price management messages
                "FcPriceSet_req" => HandleFcPriceSetRequest(root),
                "FcPriceUpdate_req" => HandleFcPriceUpdate(root),
                // Phase 7: Totals
                "FpTotals_req" => HandleFpTotals(root),
                // Phase 7: Unsupervised transactions
                "FpUnsupTrans_read_req" => HandleFpUnsupTransRead(root),
                _ => HandleUnknownMessage(messageType),
            };

            return (response, null);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Failed to parse DOMS JPL message: {Payload}", Truncate(jsonPayload));
            return (null, null);
        }
    }

    private byte[] HandleFcLogon(JsonElement root, ref bool loggedIn)
    {
        string accessCode = root.TryGetProperty("accessCode", out JsonElement ac)
            ? ac.GetString() ?? string.Empty
            : string.Empty;

        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn)
            ? sn.GetInt32()
            : 0;

        DomsJplSimulatorOptions config = options.Value;

        bool accepted = !state.ErrorInjection.RejectLogon &&
                        string.Equals(accessCode, config.AcceptedAccessCode, StringComparison.Ordinal);

        if (state.ErrorInjection.RejectLogon && TryConsumeErrorShot())
        {
            accepted = false;
        }

        loggedIn = accepted;

        string response = JsonSerializer.Serialize(new
        {
            type = "FcLogon_resp",
            sequenceNumber,
            result = accepted ? 0 : 1,
            resultText = accepted ? "Logon accepted" : "Logon rejected: invalid access code",
            protocolVersion = "1.0",
            pumpCount = config.PumpCount,
        }, JsonOptions);

        logger.LogInformation(
            "FcLogon_req: accessCode={AccessCode}, accepted={Accepted}",
            accessCode.Length > 4 ? accessCode[..4] + "***" : "***",
            accepted);

        return DomsJplFrameCodec.Encode(response);
    }

    private byte[] HandleFpStatus(JsonElement root)
    {
        int? pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn)
            ? pn.GetInt32()
            : null;

        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn)
            ? sn.GetInt32()
            : 0;

        if (pumpNumber.HasValue)
        {
            DomsPumpState pumpState = state.PumpStates.GetValueOrDefault(pumpNumber.Value, DomsPumpState.Offline);
            string singleResponse = JsonSerializer.Serialize(new
            {
                type = "FpStatus_resp",
                sequenceNumber,
                result = 0,
                pumpNumber = pumpNumber.Value,
                state = (int)pumpState,
                stateText = pumpState.ToString(),
            }, JsonOptions);

            return DomsJplFrameCodec.Encode(singleResponse);
        }

        // Return all pump states
        var pumps = state.PumpStates
            .OrderBy(kv => kv.Key)
            .Select(kv => new
            {
                pumpNumber = kv.Key,
                state = (int)kv.Value,
                stateText = kv.Value.ToString(),
            })
            .ToList();

        string response = JsonSerializer.Serialize(new
        {
            type = "FpStatus_resp",
            sequenceNumber,
            result = 0,
            pumps,
        }, JsonOptions);

        return DomsJplFrameCodec.Encode(response);
    }

    private byte[] HandleFpSupTransLock(JsonElement root)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn)
            ? pn.GetInt32()
            : 0;

        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn)
            ? sn.GetInt32()
            : 0;

        // Lock the pump (set to Locked state)
        if (state.PumpStates.ContainsKey(pumpNumber))
        {
            state.PumpStates[pumpNumber] = DomsPumpState.Locked;
        }

        string response = JsonSerializer.Serialize(new
        {
            type = "FpSupTrans_lock_resp",
            sequenceNumber,
            result = 0,
            resultText = "Lock successful",
            pumpNumber,
        }, JsonOptions);

        logger.LogInformation("FpSupTrans_lock_req: pump {PumpNumber} locked.", pumpNumber);
        return DomsJplFrameCodec.Encode(response);
    }

    private byte[] HandleFpSupTransRead(JsonElement root)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn)
            ? pn.GetInt32()
            : 0;

        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn)
            ? sn.GetInt32()
            : 0;

        IReadOnlyList<SimulatedDomsTransaction> transactions = state.GetTransactions();

        // Filter by pump number if specified
        var filtered = pumpNumber > 0
            ? transactions.Where(t => t.PumpNumber == pumpNumber).ToList()
            : transactions.ToList();

        var transactionPayloads = filtered.Select(t => new
        {
            transactionId = t.TransactionId,
            pumpNumber = t.PumpNumber,
            nozzleNumber = t.NozzleNumber,
            productCode = t.ProductCode,
            volume = t.Volume,
            amount = t.Amount,
            unitPrice = t.UnitPrice,
            currencyCode = t.CurrencyCode,
            occurredAtUtc = t.OccurredAtUtc,
            transactionSequence = t.TransactionSequence,
            attendantId = t.AttendantId,
            receiptText = t.ReceiptText,
        }).ToList();

        string response = JsonSerializer.Serialize(new
        {
            type = "FpSupTrans_read_resp",
            sequenceNumber,
            result = 0,
            pumpNumber,
            transactionCount = transactionPayloads.Count,
            transactions = transactionPayloads,
        }, JsonOptions);

        logger.LogInformation(
            "FpSupTrans_read_req: pump {PumpNumber}, returning {Count} transactions.",
            pumpNumber,
            transactionPayloads.Count);

        return DomsJplFrameCodec.Encode(response);
    }

    private byte[] HandleFpSupTransClear(JsonElement root)
    {
        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn)
            ? sn.GetInt32()
            : 0;

        List<string> transactionIds = [];
        if (root.TryGetProperty("transactionIds", out JsonElement tidsElement) &&
            tidsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement tidElement in tidsElement.EnumerateArray())
            {
                string? tid = tidElement.GetString();
                if (!string.IsNullOrEmpty(tid))
                {
                    transactionIds.Add(tid);
                }
            }
        }

        int cleared = transactionIds.Count > 0
            ? state.ClearTransactions(transactionIds)
            : state.ClearTransactions();

        string response = JsonSerializer.Serialize(new
        {
            type = "FpSupTrans_clear_resp",
            sequenceNumber,
            result = 0,
            resultText = $"Cleared {cleared} transaction(s)",
            clearedCount = cleared,
        }, JsonOptions);

        logger.LogInformation("FpSupTrans_clear_req: cleared {Count} transactions.", cleared);
        return DomsJplFrameCodec.Encode(response);
    }

    private async Task<byte[]> HandleAuthorizeFpAsync(JsonElement root, CancellationToken cancellationToken)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn)
            ? pn.GetInt32()
            : 0;

        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn)
            ? sn.GetInt32()
            : 0;

        decimal amount = root.TryGetProperty("amount", out JsonElement amountElement)
            ? amountElement.GetDecimal()
            : 0m;

        string correlationId = root.TryGetProperty("correlationId", out JsonElement cid)
            ? cid.GetString() ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");

        string? siteCode = root.TryGetProperty("siteCode", out JsonElement sc)
            ? sc.GetString()
            : null;

        bool rejected = state.ErrorInjection.RejectAuthorize && TryConsumeErrorShot();

        string? authCode = null;
        DateTimeOffset? expiresAt = null;

        if (!rejected && state.PumpStates.ContainsKey(pumpNumber))
        {
            state.PumpStates[pumpNumber] = DomsPumpState.Authorized;
            state.ActivePreAuths[pumpNumber] = new SimulatedDomsPreAuth
            {
                PumpNumber = pumpNumber,
                AuthorizedAmount = amount,
                CorrelationId = correlationId,
                AuthorizedAtUtc = DateTimeOffset.UtcNow,
            };

            // Bridge to shared PreAuthSimulationService so session is visible in VirtualLab UI
            string resolvedSiteCode = siteCode ?? options.Value.DefaultSiteCode ?? "SITE001";
            try
            {
                await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
                IPreAuthSimulationService preAuthService = scope.ServiceProvider.GetRequiredService<IPreAuthSimulationService>();

                string preauthId = $"JPL-{pumpNumber}-{correlationId}";
                string requestBody = JsonSerializer.Serialize(new
                {
                    preauthId,
                    correlationId,
                    pump = pumpNumber,
                    nozzle = 1,
                    amount,
                }, JsonOptions);

                Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["siteCode"] = resolvedSiteCode,
                    ["preauthId"] = preauthId,
                    ["correlationId"] = correlationId,
                    ["pump"] = pumpNumber.ToString(),
                    ["nozzle"] = "1",
                    ["amount"] = amount.ToString(),
                };

                PreAuthSimulationResponse preAuthResponse = await preAuthService.HandleAsync(
                    new PreAuthSimulationRequest(
                        resolvedSiteCode,
                        "preauth-create",
                        "POST",
                        "/doms-jpl/authorize_Fp",
                        correlationId,
                        requestBody,
                        fields),
                    cancellationToken);

                // Extract auth code and expiry from shared service response
                if (!string.IsNullOrWhiteSpace(preAuthResponse.Body))
                {
                    using JsonDocument respDoc = JsonDocument.Parse(preAuthResponse.Body);
                    if (respDoc.RootElement.TryGetProperty("authorizationCode", out JsonElement acEl) && acEl.ValueKind == JsonValueKind.String)
                        authCode = acEl.GetString();
                    if (respDoc.RootElement.TryGetProperty("expiresAtUtc", out JsonElement expEl) && expEl.ValueKind == JsonValueKind.String)
                        if (DateTimeOffset.TryParse(expEl.GetString(), out DateTimeOffset parsed))
                            expiresAt = parsed;
                    if (respDoc.RootElement.TryGetProperty("expiresAt", out JsonElement expEl2) && expEl2.ValueKind == JsonValueKind.String)
                        if (DateTimeOffset.TryParse(expEl2.GetString(), out DateTimeOffset parsed2))
                            expiresAt = parsed2;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to bridge authorize_Fp_req to PreAuthSimulationService for pump {Pump}", pumpNumber);
            }
        }

        int result = rejected ? 1 : 0;
        string resultText = rejected ? "Authorization rejected" : "Authorization accepted";
        authCode ??= $"AUTH-{Guid.NewGuid().ToString()[..8].ToUpper()}";

        string response = JsonSerializer.Serialize(new
        {
            type = "authorize_Fp_resp",
            sequenceNumber,
            result,
            resultText,
            pumpNumber,
            amount,
            correlationId,
            data = new
            {
                ResultCode = result,
                AuthCode = result == 0 ? authCode : (string?)null,
                ExpiresAt = result == 0 ? expiresAt?.ToString("o") : null,
                CorrelationId = correlationId,
            },
        }, JsonOptions);

        logger.LogInformation(
            "authorize_Fp_req: pump {PumpNumber}, amount {Amount}, result={Result}",
            pumpNumber,
            amount,
            resultText);

        return DomsJplFrameCodec.Encode(response);
    }

    private async Task<byte[]> HandleDeauthorizeFpAsync(JsonElement root, CancellationToken cancellationToken)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn)
            ? pn.GetInt32()
            : 0;

        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn)
            ? sn.GetInt32()
            : 0;

        string? siteCode = root.TryGetProperty("siteCode", out JsonElement sc)
            ? sc.GetString()
            : null;

        // Remove from local state
        state.ActivePreAuths.TryRemove(pumpNumber, out SimulatedDomsPreAuth? removedPreAuth);
        if (state.PumpStates.ContainsKey(pumpNumber))
        {
            state.PumpStates[pumpNumber] = DomsPumpState.Idle;
        }

        string correlationId = removedPreAuth?.CorrelationId ?? Guid.NewGuid().ToString("N");

        // Bridge to shared PreAuthSimulationService
        string resolvedSiteCode = siteCode ?? options.Value.DefaultSiteCode ?? "SITE001";
        try
        {
            await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
            IPreAuthSimulationService preAuthService = scope.ServiceProvider.GetRequiredService<IPreAuthSimulationService>();

            string preauthId = $"JPL-{pumpNumber}-{correlationId}";
            string requestBody = JsonSerializer.Serialize(new
            {
                preauthId,
                correlationId,
            }, JsonOptions);

            Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
            {
                ["siteCode"] = resolvedSiteCode,
                ["preauthId"] = preauthId,
                ["correlationId"] = correlationId,
            };

            await preAuthService.HandleAsync(
                new PreAuthSimulationRequest(
                    resolvedSiteCode,
                    "preauth-cancel",
                    "POST",
                    "/doms-jpl/deauthorize_Fp",
                    correlationId,
                    requestBody,
                    fields),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to bridge deauthorize_Fp_req to PreAuthSimulationService for pump {Pump}", pumpNumber);
        }

        string response = JsonSerializer.Serialize(new
        {
            type = "deauthorize_Fp_resp",
            sequenceNumber,
            result = 0,
            resultText = "Deauthorization accepted",
            pumpNumber,
            correlationId,
        }, JsonOptions);

        logger.LogInformation("deauthorize_Fp_req: pump {PumpNumber} deauthorized.", pumpNumber);
        return DomsJplFrameCodec.Encode(response);
    }

    // ---- Phase 7: Pump Control Handlers ----

    private byte[] HandleFpEmergencyStop(JsonElement root)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn) ? pn.GetInt32() : 0;
        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn) ? sn.GetInt32() : 0;

        if (state.PumpStates.ContainsKey(pumpNumber))
        {
            state.PumpStates[pumpNumber] = DomsPumpState.Error;
        }

        string response = JsonSerializer.Serialize(new
        {
            type = "FpEmergencyStop_resp",
            sequenceNumber,
            result = 0,
            resultText = "Emergency stop applied",
            pumpNumber,
        }, JsonOptions);

        logger.LogInformation("FpEmergencyStop_req: pump {PumpNumber} → Error state.", pumpNumber);
        return DomsJplFrameCodec.Encode(response);
    }

    private byte[] HandleFpCancelEmergencyStop(JsonElement root)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn) ? pn.GetInt32() : 0;
        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn) ? sn.GetInt32() : 0;

        if (state.PumpStates.ContainsKey(pumpNumber))
        {
            state.PumpStates[pumpNumber] = DomsPumpState.Idle;
        }

        string response = JsonSerializer.Serialize(new
        {
            type = "FpCancelEmergencyStop_resp",
            sequenceNumber,
            result = 0,
            resultText = "Emergency stop cancelled",
            pumpNumber,
        }, JsonOptions);

        logger.LogInformation("FpCancelEmergencyStop_req: pump {PumpNumber} → Idle state.", pumpNumber);
        return DomsJplFrameCodec.Encode(response);
    }

    private byte[] HandleFpClose(JsonElement root)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn) ? pn.GetInt32() : 0;
        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn) ? sn.GetInt32() : 0;

        if (state.PumpStates.ContainsKey(pumpNumber))
        {
            state.PumpStates[pumpNumber] = DomsPumpState.Closed;
        }

        string response = JsonSerializer.Serialize(new
        {
            type = "FpClose_resp",
            sequenceNumber,
            result = 0,
            resultText = "Pump closed",
            pumpNumber,
        }, JsonOptions);

        logger.LogInformation("FpClose_req: pump {PumpNumber} → Closed state.", pumpNumber);
        return DomsJplFrameCodec.Encode(response);
    }

    private byte[] HandleFpOpen(JsonElement root)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn) ? pn.GetInt32() : 0;
        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn) ? sn.GetInt32() : 0;

        if (state.PumpStates.ContainsKey(pumpNumber))
        {
            state.PumpStates[pumpNumber] = DomsPumpState.Idle;
        }

        string response = JsonSerializer.Serialize(new
        {
            type = "FpOpen_resp",
            sequenceNumber,
            result = 0,
            resultText = "Pump opened",
            pumpNumber,
        }, JsonOptions);

        logger.LogInformation("FpOpen_req: pump {PumpNumber} → Idle state.", pumpNumber);
        return DomsJplFrameCodec.Encode(response);
    }

    // ---- Phase 7: Price Management Handlers ----

    private byte[] HandleFcPriceSetRequest(JsonElement root)
    {
        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn) ? sn.GetInt32() : 0;

        SimulatedPriceSet priceSet = state.PriceSet;
        var grades = priceSet.GradePrices.Values.Select(g => new
        {
            gradeId = g.GradeId,
            gradeName = g.GradeName,
            priceMinorUnits = g.PriceMinorUnits,
            currencyCode = g.CurrencyCode,
        }).ToList();

        string response = JsonSerializer.Serialize(new
        {
            type = "FcPriceSet_resp",
            sequenceNumber,
            result = 0,
            priceSetId = priceSet.PriceSetId,
            gradeCount = grades.Count,
            grades,
            lastUpdatedAtUtc = priceSet.LastUpdatedAtUtc,
        }, JsonOptions);

        logger.LogInformation("FcPriceSet_req: returning {GradeCount} grade prices.", grades.Count);
        return DomsJplFrameCodec.Encode(response);
    }

    private byte[] HandleFcPriceUpdate(JsonElement root)
    {
        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn) ? sn.GetInt32() : 0;

        if (root.TryGetProperty("grades", out JsonElement gradesElement) &&
            gradesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement gradeElement in gradesElement.EnumerateArray())
            {
                string gradeId = gradeElement.TryGetProperty("gradeId", out JsonElement gid) ? gid.GetString() ?? "" : "";
                long price = gradeElement.TryGetProperty("priceMinorUnits", out JsonElement p) ? p.GetInt64() : 0;

                if (!string.IsNullOrEmpty(gradeId) && state.PriceSet.GradePrices.TryGetValue(gradeId, out var existing))
                {
                    existing.PriceMinorUnits = price;
                }
                else if (!string.IsNullOrEmpty(gradeId))
                {
                    state.PriceSet.GradePrices[gradeId] = new SimulatedGradePrice
                    {
                        GradeId = gradeId,
                        PriceMinorUnits = price,
                    };
                }
            }

            state.PriceSet.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        string response = JsonSerializer.Serialize(new
        {
            type = "FcPriceUpdate_resp",
            sequenceNumber,
            result = 0,
            resultText = "Price update applied",
        }, JsonOptions);

        logger.LogInformation("FcPriceUpdate_req: prices updated.");
        return DomsJplFrameCodec.Encode(response);
    }

    // ---- Phase 7: Totals Handler ----

    private byte[] HandleFpTotals(JsonElement root)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn) ? pn.GetInt32() : 0;
        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn) ? sn.GetInt32() : 0;

        var totals = pumpNumber > 0
            ? state.PumpTotals.Where(kv => kv.Key == pumpNumber)
                .Select(kv => new { pumpNumber = kv.Key, totalVolumeMicrolitres = kv.Value.TotalVolumeMicrolitres, totalAmountMinorUnits = kv.Value.TotalAmountMinorUnits, lastUpdatedAtUtc = kv.Value.LastUpdatedAtUtc })
                .ToList()
            : state.PumpTotals.OrderBy(kv => kv.Key)
                .Select(kv => new { pumpNumber = kv.Key, totalVolumeMicrolitres = kv.Value.TotalVolumeMicrolitres, totalAmountMinorUnits = kv.Value.TotalAmountMinorUnits, lastUpdatedAtUtc = kv.Value.LastUpdatedAtUtc })
                .ToList();

        string response = JsonSerializer.Serialize(new
        {
            type = "FpTotals_resp",
            sequenceNumber,
            result = 0,
            pumpCount = totals.Count,
            totals,
        }, JsonOptions);

        logger.LogInformation("FpTotals_req: returning totals for {Count} pump(s).", totals.Count);
        return DomsJplFrameCodec.Encode(response);
    }

    // ---- Phase 7: Unsupervised Transaction Handler ----

    private byte[] HandleFpUnsupTransRead(JsonElement root)
    {
        int pumpNumber = root.TryGetProperty("pumpNumber", out JsonElement pn) ? pn.GetInt32() : 0;
        int sequenceNumber = root.TryGetProperty("sequenceNumber", out JsonElement sn) ? sn.GetInt32() : 0;

        IReadOnlyList<SimulatedDomsTransaction> transactions = state.GetUnsupervisedTransactions();

        var filtered = pumpNumber > 0
            ? transactions.Where(t => t.PumpNumber == pumpNumber).ToList()
            : transactions.ToList();

        var transactionPayloads = filtered.Select(t => new
        {
            transactionId = t.TransactionId,
            pumpNumber = t.PumpNumber,
            nozzleNumber = t.NozzleNumber,
            productCode = t.ProductCode,
            volume = t.Volume,
            amount = t.Amount,
            unitPrice = t.UnitPrice,
            currencyCode = t.CurrencyCode,
            occurredAtUtc = t.OccurredAtUtc,
            transactionSequence = t.TransactionSequence,
            attendantId = t.AttendantId,
        }).ToList();

        // Clear the unsupervised buffer after reading (non-buffered read)
        state.ClearUnsupervisedTransactions();

        string response = JsonSerializer.Serialize(new
        {
            type = "FpUnsupTrans_read_resp",
            sequenceNumber,
            result = 0,
            pumpNumber,
            transactionCount = transactionPayloads.Count,
            transactions = transactionPayloads,
        }, JsonOptions);

        logger.LogInformation(
            "FpUnsupTrans_read_req: pump {PumpNumber}, returning {Count} unsupervised transactions.",
            pumpNumber,
            transactionPayloads.Count);

        return DomsJplFrameCodec.Encode(response);
    }

    private byte[] HandleUnknownMessage(string messageType)
    {
        logger.LogWarning("Unknown DOMS JPL message type: {MessageType}", messageType);

        string response = JsonSerializer.Serialize(new
        {
            type = "error_resp",
            result = 99,
            resultText = $"Unknown message type: {messageType}",
        }, JsonOptions);

        return DomsJplFrameCodec.Encode(response);
    }

    /// <summary>
    /// Sends an unsolicited push message (pump state change or transaction available)
    /// to all connected clients. Called from management endpoints.
    /// </summary>
    public async Task SendUnsolicitedPushAsync(string messageType, object payload, CancellationToken cancellationToken)
    {
        string message = BuildUnsolicitedPayload(messageType, payload);
        byte[] frame = DomsJplFrameCodec.Encode(message);
        ClientSession[] sessions = _sessions.Values
            .Where(x => x.IsLoggedIn && x.Client.Connected)
            .ToArray();

        int delivered = 0;
        foreach (ClientSession session in sessions)
        {
            try
            {
                await session.SendAsync(frame, cancellationToken);
                delivered++;
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
            {
                logger.LogWarning(
                    exception,
                    "Failed to deliver unsolicited DOMS push {MessageType} to {Endpoint}.",
                    messageType,
                    session.RemoteEndpoint);
            }
        }

        logger.LogInformation(
            "Unsolicited push delivered: {MessageType} to {DeliveredCount} logged-in client(s).",
            messageType,
            delivered);
    }

    private bool TryConsumeErrorShot()
    {
        DomsErrorInjection injection = state.ErrorInjection;
        if (injection.ShotCount <= 0)
        {
            return true; // Unlimited
        }

        if (injection.ShotsRemaining <= 0)
        {
            return false; // Exhausted
        }

        injection.ShotsRemaining--;

        // Auto-clear when exhausted
        if (injection.ShotsRemaining <= 0)
        {
            state.ErrorInjection = new DomsErrorInjection();
        }

        return true;
    }

    private static string Truncate(string value, int maxLength = 200)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    private string BuildUnsolicitedPayload(string messageType, object payload)
    {
        if (payload is PushNotificationRequest request)
        {
            DomsPumpState? pumpState = null;
            if (!string.IsNullOrWhiteSpace(request.State) &&
                Enum.TryParse(request.State, ignoreCase: true, out DomsPumpState parsedState))
            {
                pumpState = parsedState;
            }

            return JsonSerializer.Serialize(new
            {
                type = messageType,
                pumpNumber = request.PumpNumber,
                state = request.State,
                stateCode = pumpState.HasValue ? (int?)pumpState.Value : null,
                amount = request.Amount,
                volume = request.Volume,
                occurredAtUtc = DateTimeOffset.UtcNow,
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            type = messageType,
            payload,
            occurredAtUtc = DateTimeOffset.UtcNow,
        }, JsonOptions);
    }

    private sealed class ClientSession(Guid id, TcpClient client, NetworkStream stream, string remoteEndpoint)
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public Guid Id { get; } = id;
        public TcpClient Client { get; } = client;
        public string RemoteEndpoint { get; } = remoteEndpoint;
        public bool IsLoggedIn { get; set; }

        public async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await stream.WriteAsync(frame, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
