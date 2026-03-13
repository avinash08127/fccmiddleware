using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.WebSocket;

/// <summary>
/// Handler methods for each WebSocket <c>mode</c> command from Odoo POS.
/// Each method creates a scoped <see cref="AgentDbContext"/> to ensure
/// thread safety and proper EF Core lifecycle.
/// </summary>
internal sealed class OdooWsMessageHandler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Func<string, object?, CancellationToken, Task> _broadcastToAll;

    public OdooWsMessageHandler(
        IServiceScopeFactory scopeFactory,
        ILogger logger,
        JsonSerializerOptions jsonOptions,
        Func<string, object?, CancellationToken, Task> broadcastToAll)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _jsonOptions = jsonOptions;
        _broadcastToAll = broadcastToAll;
    }

    // ── latest ──────────────────────────────────────────────────────────────

    public async Task HandleLatestAsync(
        System.Net.WebSockets.WebSocket ws,
        JsonElement root,
        CancellationToken ct)
    {
        int? pumpId = root.TryGetProperty("pump_id", out var p) && p.TryGetInt32(out var pv) ? pv : null;
        int? nozzleId = root.TryGetProperty("nozzle_id", out var n) && n.TryGetInt32(out var nv) ? nv : null;
        string? emp = root.TryGetProperty("emp", out var e) ? e.GetString() : null;
        string? createdDate = root.TryGetProperty("CreatedDate", out var cd) ? cd.GetString() : null;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var query = db.Transactions
            .Where(t => t.SyncStatus != SyncStatus.SyncedToOdoo && t.SyncStatus != SyncStatus.Archived);

        if (pumpId.HasValue) query = query.Where(t => t.PumpNumber == pumpId.Value);
        if (nozzleId.HasValue) query = query.Where(t => t.NozzleNumber == nozzleId.Value);
        if (!string.IsNullOrEmpty(emp)) query = query.Where(t => t.AttendantId == emp);
        if (DateTimeOffset.TryParse(createdDate, out var since))
            query = query.Where(t => t.CreatedAt >= since);

        var txns = await query
            .OrderByDescending(t => t.CompletedAt)
            .Take(200)
            .ToListAsync(ct);

        var dtos = txns.Select(t => t.ToWsDto()).ToList();
        await SendAsync(ws, new { type = "latest", data = dtos.Count > 0 ? (object)dtos : null }, ct);
    }

    // ── all ──────────────────────────────────────────────────────────────────

    public async Task HandleAllAsync(
        System.Net.WebSockets.WebSocket ws,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var txns = await db.Transactions
            .OrderByDescending(t => t.CompletedAt)
            .Take(500)
            .ToListAsync(ct);

        var dtos = txns.Select(t => t.ToWsDto()).ToList();
        await SendAsync(ws, new { type = "all_transactions", data = dtos }, ct);
    }

    // ── manager_update ──────────────────────────────────────────────────────
    // T-DSK-014: Delegates DB mutation to ITransactionUpdateService so business
    // logic is testable and reusable outside the WebSocket transport layer.

    public async Task HandleManagerUpdateAsync(
        System.Net.WebSockets.WebSocket ws,
        JsonElement root,
        CancellationToken ct)
    {
        var txId = root.TryGetProperty("transaction_id", out var t) ? t.GetString() : null;
        if (txId is null || !root.TryGetProperty("update", out var update)) return;

        var fields = ParseUpdateFields(update);

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITransactionUpdateService>();
        var tx = await svc.ApplyManagerUpdateAsync(txId, fields, ct);
        if (tx is null) return;

        // Skip broadcast for add_to_cart-only updates (legacy fix)
        var isOnlyAddToCart = update.EnumerateObject().Count() == 1 &&
                              update.TryGetProperty("add_to_cart", out _);
        if (isOnlyAddToCart) return;

        await _broadcastToAll("transaction_update", tx.ToWsDto(), ct);
    }

    // ── attendant_update ────────────────────────────────────────────────────

    public async Task HandleAttendantUpdateAsync(
        System.Net.WebSockets.WebSocket ws,
        JsonElement root,
        CancellationToken ct)
    {
        var txId = root.TryGetProperty("transaction_id", out var t) ? t.GetString() : null;
        if (txId is null || !root.TryGetProperty("update", out var update)) return;

        var fields = ParseUpdateFields(update);

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITransactionUpdateService>();
        var (tx, shouldBroadcast) = await svc.ApplyAttendantUpdateAsync(txId, fields, ct);

        if (tx is not null && shouldBroadcast)
            await _broadcastToAll("transaction_update", tx.ToWsDto(), ct);
    }

    // ── FuelPumpStatus ──────────────────────────────────────────────────────

    public async Task HandleFuelPumpStatusAsync(
        System.Net.WebSockets.WebSocket ws,
        IPumpStatusService? pumpStatusService,
        CancellationToken ct)
    {
        if (pumpStatusService is null)
        {
            await SendAsync(ws, new { type = "FuelPumpStatus", data = (object?)null }, ct);
            return;
        }

        var result = await pumpStatusService.GetPumpStatusAsync(null, ct);
        foreach (var status in result.Pumps)
        {
            var dto = status.ToWsDto();
            await SendAsync(ws, dto, ct);
        }
    }

    // ── fp_unblock ──────────────────────────────────────────────────────────

    public async Task HandleFpUnblockAsync(
        System.Net.WebSockets.WebSocket ws,
        JsonElement root,
        CancellationToken ct)
    {
        var fpId = root.TryGetProperty("fp_id", out var f) && f.TryGetInt32(out var fv) ? fv : 0;
        await SendAsync(ws, new
        {
            type = "fp_unblock",
            data = new { fp_id = fpId, state = "unblocked", message = "Pump unblock processed" }
        }, ct);
    }

    // ── attendant_pump_count_update ─────────────────────────────────────────

    public async Task HandleAttendantPumpCountUpdateAsync(
        System.Net.WebSockets.WebSocket ws,
        JsonElement root,
        CancellationToken ct)
    {
        if (!root.TryGetProperty("data", out var data)) return;

        var items = JsonSerializer.Deserialize<List<AttendantPumpCountUpdateItem>>(
            data.GetRawText(), _jsonOptions);
        if (items is null) return;

        foreach (var item in items)
        {
            await SendAsync(ws, new
            {
                type = "attendant_pump_count_update_ack",
                data = new
                {
                    pump_number = item.PumpNumber,
                    emp_tag_no = item.EmpTagNo,
                    max_limit = item.NewMaxTransaction,
                    status = "updated"
                }
            }, ct);
        }
    }

    // ── manager_manual_update ───────────────────────────────────────────────

    public async Task HandleManagerManualUpdateAsync(
        System.Net.WebSockets.WebSocket ws,
        JsonElement root,
        CancellationToken ct)
    {
        var txId = root.TryGetProperty("transaction_id", out var t) ? t.GetString() : null;
        if (txId is null) return;

        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ITransactionUpdateService>();
        await svc.DiscardTransactionAsync(txId, ct);

        await SendAsync(ws, new
        {
            type = "transaction_update",
            data = new { transaction_id = txId, state = "approved", manual_approved = "yes" }
        }, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static TransactionUpdateFields ParseUpdateFields(JsonElement update) => new()
    {
        OrderUuid = update.TryGetProperty("order_uuid", out var ou) ? ou.GetString() : null,
        OdooOrderId = update.TryGetProperty("order_id", out var oi) ? oi.ToString() : null,
        PaymentId = update.TryGetProperty("payment_id", out var pi) ? pi.GetString() : null,
        AddToCart = update.TryGetProperty("add_to_cart", out var ac)
            && (ac.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? ac.GetBoolean()
                : null,
    };

    private async Task SendAsync(
        System.Net.WebSockets.WebSocket ws,
        object data,
        CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
