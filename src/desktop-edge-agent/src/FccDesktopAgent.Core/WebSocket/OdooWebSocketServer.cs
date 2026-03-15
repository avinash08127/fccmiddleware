using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.WebSocket;

/// <summary>
/// Odoo backward-compatible WebSocket server using ASP.NET Core Kestrel.
///
/// Mimics the DOMSRealImplementation Fleck-based WSS server protocol so Odoo POS
/// requires zero code changes. Listens on configurable port (default 8443).
///
/// Per-connection pump status timer fires every 3 seconds, sending each
/// <see cref="FuelPumpStatusWsDto"/> individually to the specific client.
/// </summary>
public sealed class OdooWebSocketServer : IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OdooWebSocketServer> _logger;

    private readonly ConcurrentDictionary<System.Net.WebSockets.WebSocket, CancellationTokenSource> _clients = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = null, // exact field names from [JsonPropertyName]
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // P-DSK-012/020: Single shared pump status broadcast loop instead of per-connection loops.
    private CancellationTokenSource? _broadcastLoopCts;
    private Task? _broadcastLoopTask;
    private readonly object _broadcastLoopLock = new();

    /// <summary>Late-bound: wired when FCC adapter becomes available.</summary>
    public IFccAdapter? FccAdapter { get; set; }

    /// <summary>Late-bound: wired when pump status service becomes available.</summary>
    public IPumpStatusService? PumpStatusService { get; set; }

    /// <summary>WebSocket server configuration.</summary>
    public WebSocketServerOptions Options { get; set; } = new();

    public OdooWebSocketServer(
        IServiceScopeFactory scopeFactory,
        ILogger<OdooWebSocketServer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Handles a single WebSocket connection. Called by Kestrel middleware.
    /// </summary>
    public async Task HandleConnectionAsync(
        System.Net.WebSockets.WebSocket webSocket,
        CancellationToken ct)
    {
        if (_clients.Count >= Options.MaxConnections)
        {
            _logger.LogWarning("Max WebSocket connections ({Max}) reached — rejecting client", Options.MaxConnections);
            await webSocket.CloseAsync(
                WebSocketCloseStatus.PolicyViolation,
                "Max connections reached",
                ct);
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _clients[webSocket] = cts;
        _logger.LogInformation("WebSocket client connected (total={Total})", _clients.Count);

        // P-DSK-012/020: Start the shared pump status broadcast loop when the first client connects
        EnsureSharedBroadcastLoopStarted(ct);

        try
        {
            await ReceiveLoopAsync(webSocket, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            cts.Cancel();
            _clients.TryRemove(webSocket, out _);
            _logger.LogInformation("WebSocket client disconnected (remaining={Remaining})", _clients.Count);

            if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Goodbye", CancellationToken.None);
                }
                catch { /* closing — safe to ignore */ }
            }
        }
    }

    // ── Receive loop ────────────────────────────────────────────────────────

    /// <summary>
    /// S-DSK-020: Maximum allowed WebSocket message size (1 MB).
    /// Messages exceeding this limit are rejected and the connection is closed.
    /// </summary>
    private const int MaxMessageSize = 1 * 1024 * 1024;

    private async Task ReceiveLoopAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                // S-DSK-020: Guard against unbounded memory allocation
                if (sb.Length > MaxMessageSize)
                {
                    _logger.LogWarning(
                        "WebSocket message exceeded maximum size ({MaxSize} bytes) — closing connection",
                        MaxMessageSize);
                    await ws.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "Message too large",
                        ct);
                    return;
                }
            }
            while (!result.EndOfMessage);

            var message = sb.ToString();
            await HandleMessageAsync(ws, message, ct);
        }
    }

    // ── Message routing (switch on `mode`) ──────────────────────────────────

    private async Task HandleMessageAsync(
        System.Net.WebSockets.WebSocket ws,
        string message,
        CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var mode = root.TryGetProperty("mode", out var m)
                ? m.GetString()?.ToLowerInvariant() ?? ""
                : "";

            var handler = new OdooWsMessageHandler(
                _scopeFactory, _logger, _jsonOptions, BroadcastToAllAsync);

            switch (mode)
            {
                case "latest":
                    await handler.HandleLatestAsync(ws, root, ct);
                    break;
                case "all":
                    await handler.HandleAllAsync(ws, ct);
                    break;
                case "manager_update":
                    await handler.HandleManagerUpdateAsync(ws, root, ct);
                    break;
                case "attendant_update":
                    await handler.HandleAttendantUpdateAsync(ws, root, ct);
                    break;
                case "fuelpumpstatus":
                    await handler.HandleFuelPumpStatusAsync(ws, PumpStatusService, ct);
                    break;
                case "fp_unblock":
                    await handler.HandleFpUnblockAsync(ws, root, FccAdapter, ct);
                    break;
                case "attendant_pump_count_update":
                    await handler.HandleAttendantPumpCountUpdateAsync(ws, root, ct);
                    break;
                case "manager_manual_update":
                    await handler.HandleManagerManualUpdateAsync(ws, root, ct);
                    break;
                case "add_transaction":
                    break; // No-op: FCC is source of truth
                default:
                    await SendAsync(ws, new { status = "error", message = $"Unknown mode '{mode}'" }, ct);
                    break;
            }
        }
        catch (JsonException)
        {
            await SendAsync(ws, new { status = "error", message = "Invalid JSON" }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling WebSocket message");
            try { await SendAsync(ws, new { status = "error", message = "Internal server error" }, ct); }
            catch { /* connection closing */ }
        }
    }

    // ── Pump status broadcast (shared site-wide loop) ───────────────────────

    // P-DSK-012/020: Single shared broadcast loop that queries pump status once
    // and sends to all connected clients, instead of per-connection loops.
    private void EnsureSharedBroadcastLoopStarted(CancellationToken ct)
    {
        lock (_broadcastLoopLock)
        {
            if (_broadcastLoopTask is not null && !_broadcastLoopTask.IsCompleted)
                return;

            _broadcastLoopCts?.Dispose();
            _broadcastLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _broadcastLoopTask = SharedPumpStatusBroadcastLoopAsync(_broadcastLoopCts.Token);
        }
    }

    private async Task SharedPumpStatusBroadcastLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, Options.PumpStatusBroadcastIntervalSeconds));
        await Task.Delay(interval, ct); // Initial delay before first broadcast

        while (!ct.IsCancellationRequested)
        {
            if (_clients.IsEmpty)
            {
                await Task.Delay(interval, ct);
                continue;
            }

            try
            {
                var svc = PumpStatusService;
                if (svc is not null)
                {
                    var result = await svc.GetPumpStatusAsync(null, ct);
                    foreach (var status in result.Pumps)
                    {
                        var dto = status.ToWsDto();
                        await BroadcastToAllAsync("FuelPumpStatus", dto, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Shared pump status broadcast failed");
            }

            await Task.Delay(interval, ct);
        }
    }

    // ── Broadcast to all ────────────────────────────────────────────────────

    // P-DSK-025: Send to all clients in parallel with bounded concurrency so one slow
    // socket doesn't delay delivery to every other terminal.
    internal async Task BroadcastToAllAsync(string type, object? data, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { type, data }, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var dead = new ConcurrentBag<System.Net.WebSockets.WebSocket>();
        var snapshot = _clients.Keys.ToArray();

        await Parallel.ForEachAsync(snapshot, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(snapshot.Length, 10),
            CancellationToken = ct,
        }, async (ws, token) =>
        {
            if (ws.State != WebSocketState.Open) { dead.Add(ws); return; }
            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
            }
            catch
            {
                dead.Add(ws);
            }
        });

        foreach (var ws in dead)
        {
            if (_clients.TryRemove(ws, out var cts))
                cts.Cancel();
        }
    }

    private async Task SendAsync(System.Net.WebSockets.WebSocket ws, object data, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public void Dispose()
    {
        _broadcastLoopCts?.Cancel();
        _broadcastLoopCts?.Dispose();

        foreach (var (ws, cts) in _clients)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _clients.Clear();
    }
}

/// <summary>
/// Configuration for the Odoo WebSocket server.
/// </summary>
public sealed class WebSocketServerOptions
{
    public const string SectionName = "WebSocket";

    public bool Enabled { get; set; }
    public int Port { get; set; } = 8443;
    public int MaxConnections { get; set; } = 10;
    public int PumpStatusBroadcastIntervalSeconds { get; set; } = 3;

    /// <summary>Whether to enable TLS (WSS) on the WebSocket port. Requires <see cref="CertificatePath"/>.</summary>
    public bool UseTls { get; set; }

    /// <summary>Absolute path to a PFX/PKCS#12 certificate file for TLS.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Password for the PFX certificate file.</summary>
    [SensitiveData]
    public string? CertificatePassword { get; set; }
}
