using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    ILogger<DomsJplSimulatorService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
                            await SendFrameAsync(stream, DomsJplFrameCodec.EncodeHeartbeat(), linkedCts.Token);
                        }

                        continue;
                    }

                    // Apply configured response delay
                    if (state.ErrorInjection.ResponseDelayMs > 0)
                    {
                        await Task.Delay(state.ErrorInjection.ResponseDelayMs, linkedCts.Token);
                    }

                    byte[]? response = ProcessMessage(frame.Payload, ref loggedIn);
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
                        await SendFrameAsync(stream, malformed, linkedCts.Token);
                        continue;
                    }

                    await SendFrameAsync(stream, response, linkedCts.Token);

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
            state.DecrementConnectedClients();
            client.Dispose();
            logger.LogInformation("DOMS JPL client disconnected: {Endpoint}", remoteEndpoint);
        }
    }

    private byte[]? ProcessMessage(string jsonPayload, ref bool loggedIn)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonPayload);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("type", out JsonElement typeElement))
            {
                logger.LogWarning("DOMS JPL message missing 'type' field: {Payload}", Truncate(jsonPayload));
                return null;
            }

            string messageType = typeElement.GetString() ?? string.Empty;

            return messageType switch
            {
                "FcLogon_req" => HandleFcLogon(root, ref loggedIn),
                "FpStatus_req" => HandleFpStatus(root),
                "FpSupTrans_lock_req" => HandleFpSupTransLock(root),
                "FpSupTrans_read_req" => HandleFpSupTransRead(root),
                "FpSupTrans_clear_req" => HandleFpSupTransClear(root),
                "authorize_Fp_req" => HandleAuthorizeFp(root),
                _ => HandleUnknownMessage(messageType),
            };
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Failed to parse DOMS JPL message: {Payload}", Truncate(jsonPayload));
            return null;
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

    private byte[] HandleAuthorizeFp(JsonElement root)
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

        bool rejected = state.ErrorInjection.RejectAuthorize && TryConsumeErrorShot();

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
        }

        int result = rejected ? 1 : 0;
        string resultText = rejected ? "Authorization rejected" : "Authorization accepted";

        string response = JsonSerializer.Serialize(new
        {
            type = "authorize_Fp_resp",
            sequenceNumber,
            result,
            resultText,
            pumpNumber,
            amount,
            correlationId,
        }, JsonOptions);

        logger.LogInformation(
            "authorize_Fp_req: pump {PumpNumber}, amount {Amount}, result={Result}",
            pumpNumber,
            amount,
            resultText);

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
        // This is a simplified approach -- in a real implementation we would track
        // all connected client streams. For the test simulator, the state change is
        // picked up on the next poll from the client.
        logger.LogInformation(
            "Unsolicited push queued: {MessageType}. Active clients will receive on next poll.",
            messageType);

        await Task.CompletedTask;
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

    private static async Task SendFrameAsync(NetworkStream stream, byte[] frame, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string Truncate(string value, int maxLength = 200)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}
