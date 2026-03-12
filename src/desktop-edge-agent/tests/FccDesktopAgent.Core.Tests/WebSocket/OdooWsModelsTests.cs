using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.WebSocket;
using FluentAssertions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.WebSocket;

/// <summary>
/// Serialization round-trip tests for WebSocket DTOs.
///
/// Validates that:
///   - Each DTO serializes to JSON with the exact legacy field names
///   - Mixed casing (snake_case, PascalCase, camelCase) is preserved
///   - Deserialization from JSON produces an equal object
///   - Null fields are handled correctly
///   - Mapper extensions produce correct conversions
/// </summary>
public sealed class OdooWsModelsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    // ── PumpTransactionWsDto ─────────────────────────────────────────────

    [Fact]
    public void PumpTransactionWsDto_RoundTrip_PreservesAllFields()
    {
        var dto = new PumpTransactionWsDto
        {
            Id = 42,
            TransactionId = "TXN-001",
            PumpId = 3,
            NozzleId = 1,
            Attendant = "EMP-100",
            ProductId = "DIESEL",
            Qty = 45.678m,
            UnitPrice = 1.25m,
            Total = 57.10m,
            State = "pending",
            StartTime = "2024-06-15T10:00:00Z",
            EndTime = "2024-06-15T10:05:00Z",
            OrderUuid = "uuid-abc-123",
            SyncStatus = 0,
            OdooOrderId = "SO-456",
            AddToCart = true,
            PaymentId = "PAY-789",
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var decoded = JsonSerializer.Deserialize<PumpTransactionWsDto>(json, JsonOptions)!;

        decoded.Id.Should().Be(42);
        decoded.TransactionId.Should().Be("TXN-001");
        decoded.PumpId.Should().Be(3);
        decoded.NozzleId.Should().Be(1);
        decoded.Attendant.Should().Be("EMP-100");
        decoded.ProductId.Should().Be("DIESEL");
        decoded.Qty.Should().Be(45.678m);
        decoded.UnitPrice.Should().Be(1.25m);
        decoded.Total.Should().Be(57.10m);
        decoded.State.Should().Be("pending");
        decoded.StartTime.Should().Be("2024-06-15T10:00:00Z");
        decoded.EndTime.Should().Be("2024-06-15T10:05:00Z");
        decoded.OrderUuid.Should().Be("uuid-abc-123");
        decoded.SyncStatus.Should().Be(0);
        decoded.OdooOrderId.Should().Be("SO-456");
        decoded.AddToCart.Should().BeTrue();
        decoded.PaymentId.Should().Be("PAY-789");
    }

    [Fact]
    public void PumpTransactionWsDto_UsesExactLegacyFieldNames()
    {
        var dto = new PumpTransactionWsDto
        {
            Id = 1,
            TransactionId = "T1",
            PumpId = 1,
            NozzleId = 1,
            ProductId = "P1",
            State = "pending",
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        var expected = new HashSet<string>
        {
            "id", "transaction_id", "pump_id", "nozzle_id", "attendant",
            "product_id", "qty", "unit_price", "total", "state",
            "start_time", "end_time", "order_uuid", "sync_status",
            "odoo_order_id", "add_to_cart", "payment_id",
        };

        keys.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void PumpTransactionWsDto_HandlesNullOptionalFields()
    {
        var dto = new PumpTransactionWsDto
        {
            Id = 1,
            TransactionId = "T1",
            Attendant = null,
            OrderUuid = null,
            OdooOrderId = null,
            PaymentId = null,
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var decoded = JsonSerializer.Deserialize<PumpTransactionWsDto>(json, JsonOptions)!;

        decoded.Attendant.Should().BeNull();
        decoded.OrderUuid.Should().BeNull();
        decoded.OdooOrderId.Should().BeNull();
        decoded.PaymentId.Should().BeNull();
    }

    [Fact]
    public void PumpTransactionWsDto_CanDecodeFromLegacyDomsJson()
    {
        var legacyJson = """
        {
            "id": 99,
            "transaction_id": "TXN-LEGACY",
            "pump_id": 2,
            "nozzle_id": 1,
            "attendant": "ATT-1",
            "product_id": "ULP95",
            "qty": 40.0,
            "unit_price": 1.50,
            "total": 60.0,
            "state": "approved",
            "start_time": "2024-01-01T08:00:00Z",
            "end_time": "2024-01-01T08:03:00Z",
            "order_uuid": "uuid-legacy",
            "sync_status": 1,
            "odoo_order_id": "SO-100",
            "add_to_cart": true,
            "payment_id": "PAY-100"
        }
        """;

        var decoded = JsonSerializer.Deserialize<PumpTransactionWsDto>(legacyJson, JsonOptions)!;

        decoded.Id.Should().Be(99);
        decoded.TransactionId.Should().Be("TXN-LEGACY");
        decoded.PumpId.Should().Be(2);
        decoded.ProductId.Should().Be("ULP95");
        decoded.Qty.Should().Be(40.0m);
        decoded.State.Should().Be("approved");
        decoded.AddToCart.Should().BeTrue();
        decoded.PaymentId.Should().Be("PAY-100");
    }

    // ── FuelPumpStatusWsDto — mixed casing is critical ───────────────────

    [Fact]
    public void FuelPumpStatusWsDto_RoundTrip_PreservesAllFields()
    {
        var dto = new FuelPumpStatusWsDto
        {
            PumpNumber = 2,
            NozzleNumber = 1,
            Status = "dispensing",
            Reading = 123.45m,
            Volume = 30.5m,
            Litre = 30.5m,
            Amount = 38.12m,
            Attendant = "EMP-200",
            Count = 5,
            FpGradeOptionNo = 3,
            UnitPriceValue = 1.25m,
            IsOnline = true,
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var decoded = JsonSerializer.Deserialize<FuelPumpStatusWsDto>(json, JsonOptions)!;

        decoded.PumpNumber.Should().Be(2);
        decoded.NozzleNumber.Should().Be(1);
        decoded.Status.Should().Be("dispensing");
        decoded.Reading.Should().Be(123.45m);
        decoded.Volume.Should().Be(30.5m);
        decoded.Litre.Should().Be(30.5m);
        decoded.Amount.Should().Be(38.12m);
        decoded.Attendant.Should().Be("EMP-200");
        decoded.Count.Should().Be(5);
        decoded.FpGradeOptionNo.Should().Be(3);
        decoded.UnitPriceValue.Should().Be(1.25m);
        decoded.IsOnline.Should().BeTrue();
    }

    [Fact]
    public void FuelPumpStatusWsDto_PreservesMixedCaseFieldNames()
    {
        var dto = new FuelPumpStatusWsDto { PumpNumber = 1, NozzleNumber = 1 };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        // snake_case fields
        keys.Should().Contain("pump_number");
        keys.Should().Contain("nozzle_number");
        keys.Should().Contain("unit_price");

        // PascalCase field — legacy DOMS contract
        keys.Should().Contain("FpGradeOptionNo");

        // camelCase field — legacy DOMS contract
        keys.Should().Contain("isOnline");

        // Verify no normalized versions exist
        keys.Should().NotContain("fp_grade_option_no");
        keys.Should().NotContain("is_online");
        keys.Should().NotContain("fpGradeOptionNo"); // camelCase variant should NOT exist
    }

    [Fact]
    public void FuelPumpStatusWsDto_HandlesNullUnitPriceAndAttendant()
    {
        var dto = new FuelPumpStatusWsDto
        {
            PumpNumber = 1,
            NozzleNumber = 1,
            UnitPriceValue = null,
            Attendant = null,
            IsOnline = false,
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var decoded = JsonSerializer.Deserialize<FuelPumpStatusWsDto>(json, JsonOptions)!;

        decoded.UnitPriceValue.Should().BeNull();
        decoded.Attendant.Should().BeNull();
        decoded.IsOnline.Should().BeFalse();
    }

    [Fact]
    public void FuelPumpStatusWsDto_CanDecodeFromLegacyDomsJson()
    {
        var legacyJson = """
        {
            "pump_number": 5,
            "nozzle_number": 2,
            "status": "dispensing",
            "reading": 456.78,
            "volume": 25.0,
            "litre": 25.0,
            "amount": 31.25,
            "attendant": "EMP-500",
            "count": 12,
            "FpGradeOptionNo": 1,
            "unit_price": 1.25,
            "isOnline": true
        }
        """;

        var decoded = JsonSerializer.Deserialize<FuelPumpStatusWsDto>(legacyJson, JsonOptions)!;

        decoded.PumpNumber.Should().Be(5);
        decoded.NozzleNumber.Should().Be(2);
        decoded.Status.Should().Be("dispensing");
        decoded.FpGradeOptionNo.Should().Be(1);
        decoded.UnitPriceValue.Should().Be(1.25m);
        decoded.IsOnline.Should().BeTrue();
    }

    // ── WsErrorResponse ──────────────────────────────────────────────────

    [Fact]
    public void WsErrorResponse_RoundTrip_PreservesFields()
    {
        var dto = new WsErrorResponse
        {
            Message = "Unknown mode 'test'",
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var decoded = JsonSerializer.Deserialize<WsErrorResponse>(json, JsonOptions)!;

        decoded.Status.Should().Be("error");
        decoded.Message.Should().Be("Unknown mode 'test'");
    }

    [Fact]
    public void WsErrorResponse_DefaultsStatusToError()
    {
        var dto = new WsErrorResponse { Message = "fail" };

        var json = JsonSerializer.Serialize(dto, JsonOptions);

        json.Should().Contain("\"status\":\"error\"");
    }

    // ── AttendantPumpCountUpdateItem — inbound PascalCase ────────────────

    [Fact]
    public void AttendantPumpCountUpdateItem_RoundTrip_PreservesFields()
    {
        var dto = new AttendantPumpCountUpdateItem
        {
            PumpNumber = 4,
            EmpTagNo = "EMP-300",
            NewMaxTransaction = 10,
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var decoded = JsonSerializer.Deserialize<AttendantPumpCountUpdateItem>(json, JsonOptions)!;

        decoded.PumpNumber.Should().Be(4);
        decoded.EmpTagNo.Should().Be("EMP-300");
        decoded.NewMaxTransaction.Should().Be(10);
    }

    [Fact]
    public void AttendantPumpCountUpdateItem_UsesPascalCaseFieldNames()
    {
        var dto = new AttendantPumpCountUpdateItem
        {
            PumpNumber = 1,
            EmpTagNo = "E1",
            NewMaxTransaction = 5,
        };

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

        keys.Should().BeEquivalentTo(new[] { "PumpNumber", "EmpTagNo", "NewMaxTransaction" });
    }

    [Fact]
    public void AttendantPumpCountUpdateItem_DeserializesFromOdooPosPayload()
    {
        var incomingJson = """{"PumpNumber": 3, "EmpTagNo": "ATT-99", "NewMaxTransaction": 20}""";

        var decoded = JsonSerializer.Deserialize<AttendantPumpCountUpdateItem>(incomingJson, JsonOptions)!;

        decoded.PumpNumber.Should().Be(3);
        decoded.EmpTagNo.Should().Be("ATT-99");
        decoded.NewMaxTransaction.Should().Be(20);
    }

    // ── ToWsDto mappers ──────────────────────────────────────────────────

    [Fact]
    public void BufferedTransaction_ToWsDto_ConvertsMonetaryValues()
    {
        var tx = new BufferedTransaction
        {
            FccTransactionId = "TX-100",
            PumpNumber = 2,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            VolumeMicrolitres = 45_678_000L, // 45.678 litres
            UnitPriceMinorPerLitre = 125L,    // 1.25 major units
            AmountMinorUnits = 5710L,          // 57.10 major units
            SyncStatus = SyncStatus.Pending,
            StartedAt = new DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2024, 6, 15, 10, 5, 0, TimeSpan.Zero),
        };

        var dto = tx.ToWsDto();

        dto.TransactionId.Should().Be("TX-100");
        dto.PumpId.Should().Be(2);
        dto.NozzleId.Should().Be(1);
        dto.ProductId.Should().Be("DIESEL");
        dto.Qty.Should().Be(45.678m);
        dto.UnitPrice.Should().Be(1.25m);
        dto.Total.Should().Be(57.10m);
    }

    [Fact]
    public void BufferedTransaction_ToWsDto_MapsPendingState()
    {
        var tx = new BufferedTransaction
        {
            SyncStatus = SyncStatus.Pending,
            IsDiscard = false,
        };

        var dto = tx.ToWsDto();

        dto.State.Should().Be("pending");
        dto.SyncStatus.Should().Be(0);
    }

    [Fact]
    public void BufferedTransaction_ToWsDto_MapsApprovedState()
    {
        var tx = new BufferedTransaction
        {
            SyncStatus = SyncStatus.SyncedToOdoo,
            IsDiscard = false,
        };

        var dto = tx.ToWsDto();

        dto.State.Should().Be("approved");
        dto.SyncStatus.Should().Be(1);
    }

    [Fact]
    public void BufferedTransaction_ToWsDto_MapsDiscardState()
    {
        var tx = new BufferedTransaction
        {
            SyncStatus = SyncStatus.Uploaded,
            IsDiscard = true,
        };

        var dto = tx.ToWsDto();

        dto.State.Should().Be("discard");
    }

    [Fact]
    public void BufferedTransaction_ToWsDto_PreservesOdooFields()
    {
        var tx = new BufferedTransaction
        {
            OrderUuid = "uuid-abc",
            OdooOrderId = "SO-123",
            AddToCart = true,
            PaymentId = "PAY-456",
            AttendantId = "ATT-1",
        };

        var dto = tx.ToWsDto();

        dto.OrderUuid.Should().Be("uuid-abc");
        dto.OdooOrderId.Should().Be("SO-123");
        dto.AddToCart.Should().BeTrue();
        dto.PaymentId.Should().Be("PAY-456");
        dto.Attendant.Should().Be("ATT-1");
    }

    [Fact]
    public void PumpStatus_ToWsDto_MapsIdleState()
    {
        var ps = new PumpStatus
        {
            PumpNumber = 3,
            NozzleNumber = 1,
            State = PumpState.Idle,
        };

        var dto = ps.ToWsDto();

        dto.PumpNumber.Should().Be(3);
        dto.NozzleNumber.Should().Be(1);
        dto.Status.Should().Be("idle");
        dto.IsOnline.Should().BeTrue();
    }

    [Fact]
    public void PumpStatus_ToWsDto_MapsDispensingState()
    {
        var ps = new PumpStatus
        {
            PumpNumber = 1,
            NozzleNumber = 2,
            State = PumpState.Dispensing,
            CurrentVolumeLitres = "25.5",
            CurrentAmount = "31.875",
            UnitPrice = "1.25",
        };

        var dto = ps.ToWsDto();

        dto.Status.Should().Be("dispensing");
        dto.Volume.Should().Be(25.5m);
        dto.Litre.Should().Be(25.5m);
        dto.Amount.Should().Be(31.875m);
        dto.UnitPriceValue.Should().Be(1.25m);
        dto.IsOnline.Should().BeTrue();
    }

    [Fact]
    public void PumpStatus_ToWsDto_MapsOfflineState()
    {
        var ps = new PumpStatus
        {
            PumpNumber = 4,
            NozzleNumber = 1,
            State = PumpState.Offline,
        };

        var dto = ps.ToWsDto();

        dto.Status.Should().Be("offline");
        dto.IsOnline.Should().BeFalse();
    }

    [Fact]
    public void PumpStatus_ToWsDto_MapsErrorState()
    {
        var ps = new PumpStatus
        {
            PumpNumber = 5,
            NozzleNumber = 1,
            State = PumpState.Error,
        };

        var dto = ps.ToWsDto();

        dto.Status.Should().Be("inoperative");
        dto.IsOnline.Should().BeFalse();
    }

    [Fact]
    public void PumpStatus_ToWsDto_HandlesNullStringValues()
    {
        var ps = new PumpStatus
        {
            PumpNumber = 1,
            NozzleNumber = 1,
            State = PumpState.Idle,
            CurrentVolumeLitres = null,
            CurrentAmount = null,
            UnitPrice = null,
        };

        var dto = ps.ToWsDto();

        dto.Volume.Should().Be(0m);
        dto.Litre.Should().Be(0m);
        dto.Amount.Should().Be(0m);
        dto.UnitPriceValue.Should().BeNull();
    }
}
