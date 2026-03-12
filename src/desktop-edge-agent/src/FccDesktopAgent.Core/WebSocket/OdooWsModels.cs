using System.Text.Json.Serialization;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer.Entities;

namespace FccDesktopAgent.Core.WebSocket;

/// <summary>
/// Transaction DTO matching the legacy DOMSRealImplementation PumpTransactions model.
/// JSON field names MUST match the legacy contract exactly — Odoo POS parses them by name.
/// </summary>
public sealed class PumpTransactionWsDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("transaction_id")]
    public string TransactionId { get; set; } = string.Empty;

    [JsonPropertyName("pump_id")]
    public int PumpId { get; set; }

    [JsonPropertyName("nozzle_id")]
    public int NozzleId { get; set; }

    [JsonPropertyName("attendant")]
    public string? Attendant { get; set; }

    [JsonPropertyName("product_id")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("qty")]
    public decimal Qty { get; set; }

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("total")]
    public decimal Total { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "pending";

    [JsonPropertyName("start_time")]
    public string StartTime { get; set; } = string.Empty;

    [JsonPropertyName("end_time")]
    public string EndTime { get; set; } = string.Empty;

    [JsonPropertyName("order_uuid")]
    public string? OrderUuid { get; set; }

    [JsonPropertyName("sync_status")]
    public int SyncStatus { get; set; }

    [JsonPropertyName("odoo_order_id")]
    public string? OdooOrderId { get; set; }

    [JsonPropertyName("add_to_cart")]
    public bool AddToCart { get; set; }

    [JsonPropertyName("payment_id")]
    public string? PaymentId { get; set; }
}

/// <summary>
/// Fuel pump status DTO matching the legacy FuelPumpStatusDto.
/// WARNING: mixed casing is intentional — legacy contract uses snake_case, PascalCase, and camelCase.
/// </summary>
public sealed class FuelPumpStatusWsDto
{
    [JsonPropertyName("pump_number")]
    public int PumpNumber { get; set; }

    [JsonPropertyName("nozzle_number")]
    public int NozzleNumber { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "idle";

    [JsonPropertyName("reading")]
    public decimal Reading { get; set; }

    [JsonPropertyName("volume")]
    public decimal Volume { get; set; }

    [JsonPropertyName("litre")]
    public decimal Litre { get; set; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("attendant")]
    public string? Attendant { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("FpGradeOptionNo")]
    public int FpGradeOptionNo { get; set; }

    [JsonPropertyName("unit_price")]
    public decimal? UnitPriceValue { get; set; }

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; set; }
}

/// <summary>
/// WebSocket error response.
/// </summary>
public sealed class WsErrorResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "error";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Attendant pump count update item (inbound from Odoo).
/// </summary>
public sealed class AttendantPumpCountUpdateItem
{
    public int PumpNumber { get; set; }
    public string EmpTagNo { get; set; } = string.Empty;
    public int NewMaxTransaction { get; set; }
}

// ---------------------------------------------------------------------------
// Mapping extensions
// ---------------------------------------------------------------------------

public static class WsDtoMappers
{
    private static int _txIdCounter;

    /// <summary>
    /// Map a <see cref="BufferedTransaction"/> to the legacy <see cref="PumpTransactionWsDto"/>.
    /// </summary>
    public static PumpTransactionWsDto ToWsDto(this BufferedTransaction tx)
    {
        var qty = tx.VolumeMicrolitres / 1_000_000m;
        var price = tx.UnitPriceMinorPerLitre / 100m;
        var total = tx.AmountMinorUnits / 100m;

        var wsState = tx.IsDiscard ? "discard"
            : tx.SyncStatus is Adapter.Common.SyncStatus.SyncedToOdoo or Adapter.Common.SyncStatus.Uploaded
                ? "approved"
                : "pending";

        var wsSyncStatus = tx.SyncStatus == Adapter.Common.SyncStatus.Pending ? 0 : 1;

        return new PumpTransactionWsDto
        {
            Id = Interlocked.Increment(ref _txIdCounter),
            TransactionId = tx.FccTransactionId,
            PumpId = tx.PumpNumber,
            NozzleId = tx.NozzleNumber,
            Attendant = tx.AttendantId,
            ProductId = tx.ProductCode,
            Qty = qty,
            UnitPrice = price,
            Total = total,
            State = wsState,
            StartTime = tx.StartedAt.ToString("o"),
            EndTime = tx.CompletedAt.ToString("o"),
            OrderUuid = tx.OrderUuid,
            SyncStatus = wsSyncStatus,
            OdooOrderId = tx.OdooOrderId,
            AddToCart = tx.AddToCart,
            PaymentId = tx.PaymentId,
        };
    }

    /// <summary>
    /// Map a <see cref="PumpStatus"/> to the legacy <see cref="FuelPumpStatusWsDto"/>.
    /// </summary>
    public static FuelPumpStatusWsDto ToWsDto(this PumpStatus ps)
    {
        _ = decimal.TryParse(ps.CurrentVolumeLitres, out var volume);
        _ = decimal.TryParse(ps.CurrentAmount, out var amount);
        decimal? price = decimal.TryParse(ps.UnitPrice, out var p) ? p : null;

        var status = ps.State switch
        {
            PumpState.Idle => "idle",
            PumpState.Authorized => "authorized",
            PumpState.Calling => "calling",
            PumpState.Dispensing => "dispensing",
            PumpState.Paused => "suspended",
            PumpState.Completed => "idle",
            PumpState.Error => "inoperative",
            PumpState.Offline => "offline",
            _ => "unknown",
        };

        return new FuelPumpStatusWsDto
        {
            PumpNumber = ps.PumpNumber,
            NozzleNumber = ps.NozzleNumber,
            Status = status,
            Reading = 0,
            Volume = volume,
            Litre = volume,
            Amount = amount,
            Attendant = null,
            Count = 0,
            FpGradeOptionNo = 0,
            UnitPriceValue = price,
            IsOnline = ps.State is not PumpState.Offline and not PumpState.Error,
        };
    }
}
