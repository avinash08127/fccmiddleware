# DOMS Adapter Enhancement Plan

> **Goal:** Achieve full feature parity with the production-tested `DOMSRealImplementation/` legacy system.
> No legacy feature should be lost in the new architecture.

---

## Implementation Status

| Phase | Priority | Status | Notes |
|-------|----------|--------|-------|
| Phase 6 (State mapping fix) | P0 | **DONE** | Kotlin `DomsFpMainState.kt` fixed: FpStarted→AUTHORIZED, FpLocked→OFFLINE |
| Phase 1 (Pump control + limits) | P0 | **DONE** | `IFccPumpControl`, `DomsPumpControlHandler`, `PumpLimitEnforcer`, DB entities (C# + Kotlin) |
| Phase 2 (Price management) | P1 | **DONE** | `IFccPriceManagement`, `DomsPriceHandler`, unsolicited `FcPriceSetChanged` (C# + Kotlin) |
| Phase 3 (Peripheral messages) | P1 | **DONE** | `IFccPeripheralMonitor`, `BnaReport`, unsolicited EPT/BNA/Dispenser events (C# + Kotlin) |
| Phase 5 (Transaction enhancements) | P1 | **DONE** | `TransactionInfoMask`, `DomsUnsupervisedTransactionHandler`, `IFccTotalsProvider`, `DomsTotalsHandler`; OdooWsMessageHandler verified (C# + Kotlin) |
| Phase 4 (Enhanced pump status) | P2 | **DONE** | `PumpStatusSupplemental` integrated into PumpStatus (C# + Kotlin), parser extraction of 16 supplemental params implemented |
| Phase 7 (VirtualLab updates) | P2 | **DONE** | Pump control, price management, peripheral push, totals, unsupervised transactions — all simulator handlers + management endpoints + E2E tests |
| Phase 8 (Cloud backend) | P2 | **DONE** | SiteDataController with BNA, totals, prices, pump control history endpoints; domain entities + DbContext; upload contracts; config schema extended |

---

## Table of Contents

1. [Phase 1 — Pump Control & Attendant Management (HIGH)](#phase-1--pump-control--attendant-management)
2. [Phase 2 — Price Management (HIGH)](#phase-2--price-management)
3. [Phase 3 — Peripheral Device Messages (HIGH)](#phase-3--peripheral-device-messages)
4. [Phase 4 — Enhanced Pump Status (MEDIUM)](#phase-4--enhanced-pump-status)
5. [Phase 5 — Transaction Enhancements (MEDIUM)](#phase-5--transaction-enhancements)
6. [Phase 6 — State Mapping Alignment (LOW)](#phase-6--state-mapping-alignment)
7. [Phase 7 — VirtualLab Simulator Updates](#phase-7--virtuallab-simulator-updates)
8. [Phase 8 — Cloud Backend Enhancements](#phase-8--cloud-backend-enhancements)
9. [Cross-Cutting Concerns](#cross-cutting-concerns)

---

## Phase 1 — Pump Control & Attendant Management

**Gaps addressed:** G-03 (Attendant limits), G-09 (Block/unblock history)

### 1.1 New Interface: `IFccPumpControl`

The existing `IFccAdapter` does not have pump control methods (block, unblock, emergency stop, soft lock). The legacy `ForecourtClient` exposes `EmergencyBlock()`, `UnblockPump()`, `SoftLock()`, and `Unlock()` via JPL messages. We need a dedicated interface so that pump control is opt-in per adapter.

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccPumpControl.cs`

```csharp
namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Optional interface for adapters that support direct pump control commands.
/// Check if your IFccAdapter also implements this interface before calling.
/// </summary>
public interface IFccPumpControl
{
    /// <summary>
    /// Emergency-stop a fuelling point. Sends FpEmergencyStop_req (JPL) or estop endpoint (REST).
    /// Equivalent to legacy ForecourtClient.EmergencyBlock().
    /// </summary>
    Task<PumpControlResult> EmergencyStopAsync(int fpId, CancellationToken ct);

    /// <summary>
    /// Cancel an emergency stop. Sends FpCancelEmergencyStop_req.
    /// Equivalent to legacy ForecourtClient.UnblockPump().
    /// </summary>
    Task<PumpControlResult> CancelEmergencyStopAsync(int fpId, CancellationToken ct);

    /// <summary>
    /// Close (soft-lock) a fuelling point. Prevents new transactions.
    /// Equivalent to legacy ForecourtClient.SoftLock().
    /// </summary>
    Task<PumpControlResult> ClosePumpAsync(int fpId, CancellationToken ct);

    /// <summary>
    /// Open (unlock) a fuelling point. Allows new transactions.
    /// Equivalent to legacy ForecourtClient.Unlock().
    /// </summary>
    Task<PumpControlResult> OpenPumpAsync(int fpId, CancellationToken ct);
}

public record PumpControlResult(bool Success, string? ErrorMessage = null);
```

**File (Kotlin):** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/common/IFccPumpControl.kt`

```kotlin
interface IFccPumpControl {
    suspend fun emergencyStop(fpId: Int): PumpControlResult
    suspend fun cancelEmergencyStop(fpId: Int): PumpControlResult
    suspend fun closePump(fpId: Int): PumpControlResult
    suspend fun openPump(fpId: Int): PumpControlResult
}

data class PumpControlResult(val success: Boolean, val errorMessage: String? = null)
```

### 1.2 JPL Protocol Handlers for Pump Control

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/Protocol/DomsPumpControlHandler.cs`

Implements JPL message construction for the 4 pump control operations. Reference the legacy `Worker.cs` lines 1360-1430 for the exact JPL message format:

```
EmergencyBlock → { name: "FpEmergencyStop_req", data: { FpId: "XX" } }
UnblockPump    → { name: "FpCancelEmergencyStop_req", data: { FpId: "XX" } }
SoftLock       → { name: "FpClose_req", data: { FpId: "XX" } }
Unlock         → { name: "FpOpen_req", data: { FpId: "XX" } }
```

Methods to implement:
| Method | JPL Request | JPL Response | Legacy Equivalent |
|--------|------------|-------------|-------------------|
| `BuildEmergencyStopRequest(fpId)` | `FpEmergencyStop_req` | `FpEmergencyStop_resp` | `EmergencyBlock()` |
| `BuildCancelEmergencyStopRequest(fpId)` | `FpCancelEmergencyStop_req` | `FpCancelEmergencyStop_resp` | `UnblockPump()` |
| `BuildCloseRequest(fpId)` | `FpClose_req` | `FpClose_resp` | `SoftLock()` |
| `BuildOpenRequest(fpId)` | `FpOpen_req` | `FpOpen_resp` | `Unlock()` |
| `ValidateControlResponse(response)` | — | check ResultCode == "0" | — |

### 1.3 Implement `IFccPumpControl` on `DomsJplAdapter`

Make `DomsJplAdapter` implement both `IFccAdapter` and `IFccPumpControl`.

**C# changes in** `DomsJplAdapter.cs`:
```csharp
public sealed class DomsJplAdapter : IFccAdapter, IFccConnectionLifecycle, IFccPumpControl, IAsyncDisposable
{
    // ... existing code ...

    public async Task<PumpControlResult> EmergencyStopAsync(int fpId, CancellationToken ct)
    {
        var request = DomsPumpControlHandler.BuildEmergencyStopRequest(fpId);
        var response = await _tcpClient.SendAsync(request, "FpEmergencyStop_resp", ct);
        return DomsPumpControlHandler.ValidateControlResponse(response);
    }

    // Repeat for CancelEmergencyStop, ClosePump, OpenPump
}
```

**Kotlin changes in** `DomsJplAdapter.kt` — same pattern with `suspend fun`.

### 1.4 Attendant Transaction Limit Service

The legacy `ForecourtClient.CheckAndApplyPumpLimitAsync()` is a **business logic layer**, not a protocol handler. It reads limits from the database, compares to current count, and calls `EmergencyBlock()` or `UnblockPump()`. This belongs in a separate service, not inside the adapter.

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Pump/PumpLimitEnforcer.cs`

```csharp
namespace FccDesktopAgent.Core.Pump;

/// <summary>
/// Enforces per-pump transaction limits by blocking/unblocking pumps
/// via the FCC adapter. Ported from legacy ForecourtClient.CheckAndApplyPumpLimitAsync().
/// </summary>
public sealed class PumpLimitEnforcer
{
    private readonly IFccPumpControl _pumpControl;
    private readonly AgentDbContext _db;
    private readonly ILogger<PumpLimitEnforcer> _logger;

    /// <summary>
    /// Check all pump limits and apply block/unblock as needed.
    /// fpId=0 means check all pumps.
    /// </summary>
    public async Task EnforceLimitsAsync(int fpId, CancellationToken ct);

    /// <summary>
    /// Override-based limit check with IsAllowed flag.
    /// Ported from legacy CheckAndApplyPumpLimitAsync_IsAllowed().
    /// </summary>
    public async Task EnforceLimitsWithOverrideAsync(
        int fpId, bool isAllowedOverride, CancellationToken ct);

    /// <summary>
    /// Reset transaction count for a pump and re-evaluate limits.
    /// Ported from legacy TransactionService.FpLimitReset().
    /// </summary>
    public async Task ResetLimitAsync(int fpId, int? newLimit, CancellationToken ct);
}
```

### 1.5 Pump Limit & Block/Unblock Database Entities

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Buffer/Entities/PumpLimit.cs`

```csharp
/// <summary>
/// Tracks per-pump transaction limits.
/// Ported from legacy FpLimitDto: FpId, MaxLimit, CurrentCount, Status, IsAllowed.
/// </summary>
public class PumpLimit
{
    public int FpId { get; set; }
    public int MaxLimit { get; set; }
    public int CurrentCount { get; set; }
    public string Status { get; set; } = "active";
    public bool IsAllowed { get; set; } = true;
}
```

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Buffer/Entities/PumpBlockHistory.cs`

```csharp
/// <summary>
/// Audit trail for pump block/unblock actions.
/// Ported from legacy InsertBlockUnblockHistory(fpId, actionType, source, note).
/// </summary>
public class PumpBlockHistory
{
    public int Id { get; set; }
    public int FpId { get; set; }
    public string ActionType { get; set; } = ""; // "Blocked", "Unblock"
    public string Source { get; set; } = "";      // "Middleware", "Attendant", "Manager"
    public string Note { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
```

Add both to `AgentDbContext` as `DbSet<>` and create EF migration.

### 1.6 Attendant Pump Count Tracking

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Buffer/Entities/AttendantPumpCount.cs`

```csharp
/// <summary>
/// Tracks transaction counts per attendant per pump per session.
/// Ported from legacy AttendantPumpCountUpdate: SessionId, EmpTagNo, NewMaxTransaction, PumpNumber.
/// </summary>
public class AttendantPumpCount
{
    public int Id { get; set; }
    public string SessionId { get; set; } = "";
    public string EmpTagNo { get; set; } = "";
    public int PumpNumber { get; set; }
    public int MaxTransactions { get; set; }
    public int CurrentCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 1.7 Implementation Steps

| Step | File(s) | Description |
|------|---------|-------------|
| 1 | `IFccPumpControl.cs` | Create interface |
| 2 | `DomsPumpControlHandler.cs` | JPL message builders for 4 control operations |
| 3 | `DomsJplAdapter.cs` | Implement `IFccPumpControl` |
| 4 | `PumpLimit.cs`, `PumpBlockHistory.cs`, `AttendantPumpCount.cs` | EF entities |
| 5 | `AgentDbContext.cs` | Add DbSets, create migration |
| 6 | `BufferEntityConfiguration.cs` | Add EF configs for new entities |
| 7 | `PumpLimitEnforcer.cs` | Business logic service |
| 8 | `ServiceCollectionExtensions.cs` | Register `PumpLimitEnforcer` |
| 9 | `OdooWsMessageHandler.cs` | Wire limit-reset and IsAllowed-toggle commands from Odoo WS |
| 10 | Kotlin equivalents | Mirror steps 1-3 and 7 in edge-agent |
| 11 | Tests | Unit tests for `PumpLimitEnforcer`, `DomsPumpControlHandler` |

### 1.8 Test Plan

- **Unit:** `DomsPumpControlHandler` builds correct JPL JSON for each control type
- **Unit:** `PumpLimitEnforcer` calls `EmergencyStopAsync` when count >= max
- **Unit:** `PumpLimitEnforcer` calls `CancelEmergencyStopAsync` when count < max and pump is unavailable
- **Unit:** `PumpLimitEnforcer` records `PumpBlockHistory` for every block/unblock action
- **Unit:** IsAllowed override bypasses count check
- **Integration:** VirtualLab simulator responds to pump control messages and changes pump state
- **Regression:** Verify existing pre-auth and transaction flows are unaffected

---

## Phase 2 — Price Management

**Gap addressed:** G-02

### 2.1 New Interface: `IFccPriceManagement`

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccPriceManagement.cs`

```csharp
namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Optional interface for adapters that support price queries and updates.
/// </summary>
public interface IFccPriceManagement
{
    /// <summary>
    /// Request current price set from the FCC.
    /// Equivalent to legacy ForecourtClient.RequestFcPriceSet().
    /// </summary>
    Task<PriceSetSnapshot?> GetCurrentPricesAsync(CancellationToken ct);

    /// <summary>
    /// Push a price update to the FCC.
    /// Equivalent to legacy ForecourtClient.SendDynamicPriceUpdate().
    /// </summary>
    Task<PriceUpdateResult> UpdatePricesAsync(PriceUpdateCommand command, CancellationToken ct);
}
```

### 2.2 Price Models

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/PriceModels.cs`

```csharp
/// <summary>
/// Snapshot of current fuel prices from the FCC.
/// Ported from legacy DomsPriceSet: PriceSetId, PriceGroupIds, GradeIds, CurrentPrices.
/// </summary>
public record PriceSetSnapshot(
    string PriceSetId,
    IReadOnlyList<string> PriceGroupIds,
    IReadOnlyList<GradePrice> Grades,
    DateTimeOffset ObservedAtUtc);

public record GradePrice(
    string GradeId,
    string GradeName,
    long PriceMinorUnits,      // e.g. 450 = 4.50 in minor units
    string CurrencyCode);

public record PriceUpdateCommand(
    IReadOnlyList<GradePriceUpdate> Updates,
    DateTimeOffset? ActivationTime);

public record GradePriceUpdate(string GradeId, long NewPriceMinorUnits);

public record PriceUpdateResult(bool Success, string? ErrorMessage = null);
```

### 2.3 JPL Protocol Handler

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/Protocol/DomsPriceHandler.cs`

Methods to implement (reference legacy `Worker.cs` lines 303-330 and 638-700):

| Method | JPL Message | Legacy Equivalent |
|--------|------------|-------------------|
| `BuildPriceSetRequest()` | `FcPriceSet_req` | `RequestFcPriceSet()` |
| `ParsePriceSetResponse(response)` | `FcPriceSet_resp` | Parse in message handler |
| `BuildPriceUpdateRequest(updates, activationTime)` | `FcPriceUpdate_req` | `SendDynamicPriceUpdate()` |
| `ValidatePriceUpdateResponse(response)` | `FcPriceUpdate_resp` | Check ResultCode |

The legacy builds the price update message as:
```json
{
  "name": "FcPriceUpdate_req",
  "data": {
    "PriceSetId": "01",
    "GradeCount": "2",
    "Grade_0_Id": "01",
    "Grade_0_Price": "04500",
    "Grade_1_Id": "02",
    "Grade_1_Price": "05000",
    "ActivationDate": "20260217095113"
  }
}
```

### 2.4 Implement on `DomsJplAdapter`

Make `DomsJplAdapter` also implement `IFccPriceManagement`:
```csharp
public sealed class DomsJplAdapter : IFccAdapter, IFccConnectionLifecycle,
    IFccPumpControl, IFccPriceManagement, IAsyncDisposable
```

### 2.5 Price Change Event (Unsolicited)

The legacy receives `ChangeFcPriceSet` as a peripheral message (port 5006). In the JPL adapter, this will arrive as an unsolicited message. Add to `DomsJplAdapter.OnUnsolicitedMessage()`:

```csharp
case "FcPriceSetChanged":
    var priceSetId = message.Data?["PriceSetId"];
    _eventListener?.OnPriceChanged(priceSetId);
    break;
```

Extend `IFccEventListener` with:
```csharp
void OnPriceChanged(string? priceSetId) { }  // default no-op
```

### 2.6 Implementation Steps

| Step | File(s) | Description |
|------|---------|-------------|
| 1 | `IFccPriceManagement.cs`, `PriceModels.cs` | Interface and models |
| 2 | `DomsPriceHandler.cs` | JPL message builders |
| 3 | `DomsJplAdapter.cs` | Implement `IFccPriceManagement`, add unsolicited price event |
| 4 | `IFccEventListener.cs` | Add `OnPriceChanged` default method |
| 5 | Kotlin equivalents | Mirror in edge-agent |
| 6 | Tests | Unit tests for price handler, integration with VirtualLab |

### 2.7 Test Plan

- **Unit:** `DomsPriceHandler` builds correct indexed JPL format for price update
- **Unit:** `ParsePriceSetResponse` extracts all grade IDs and prices
- **Integration:** VirtualLab receives price update request and reflects new prices in state
- **Regression:** Existing adapter operations unaffected

---

## Phase 3 — Peripheral Device Messages

**Gap addressed:** G-01

### 3.1 New Interface: `IFccPeripheralMonitor`

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/IFccPeripheralMonitor.cs`

```csharp
namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Optional interface for adapters that receive peripheral device events
/// (EPT terminals, BNA devices, dispenser install data).
/// </summary>
public interface IFccPeripheralMonitor
{
    /// <summary>
    /// Request current peripheral device inventory from FCC.
    /// </summary>
    Task<PeripheralInventory> GetPeripheralInventoryAsync(CancellationToken ct);
}
```

### 3.2 Peripheral Models

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/PeripheralModels.cs`

```csharp
/// <summary>
/// Inventory of peripheral devices connected to the FCC.
/// </summary>
public record PeripheralInventory(
    IReadOnlyList<DispenserInfo> Dispensers,
    IReadOnlyList<EptTerminalInfo> EptTerminals);

/// <summary>
/// Dispenser installation data. Ported from legacy DispenserInstallData.
/// </summary>
public record DispenserInfo(string DispenserId, string Model);

/// <summary>
/// EPT (Electronic Payment Terminal) info. Ported from legacy EptInfo.
/// </summary>
public record EptTerminalInfo(string TerminalId, string Version);

/// <summary>
/// BNA (Banknote Acceptor) report. Ported from legacy EptBnaReport.
/// </summary>
public record BnaReport(string TerminalId, int NotesAccepted, DateTimeOffset ReportedAtUtc);
```

### 3.3 Peripheral Event Listener

Extend `IFccEventListener` with peripheral callbacks:

```csharp
// Add to IFccEventListener
void OnBnaReport(BnaReport report) { }               // default no-op
void OnDispenserInstallData(DispenserInfo info) { }   // default no-op
void OnEptInfoReceived(EptTerminalInfo info) { }      // default no-op
```

### 3.4 Unsolicited Message Handling in DomsJplAdapter

Add cases to `DomsJplAdapter.OnUnsolicitedMessage()`:

```csharp
case "EptBnaReport":
    var terminalId = message.Data?["TerminalId"] ?? "";
    var notesAccepted = int.Parse(message.Data?["NotesAccepted"] ?? "0");
    _eventListener?.OnBnaReport(new BnaReport(terminalId, notesAccepted, DateTimeOffset.UtcNow));
    break;

case "DispenserInstallData":
    var dispenserId = message.Data?["DispenserId"] ?? "";
    var model = message.Data?["Model"] ?? "";
    _eventListener?.OnDispenserInstallData(new DispenserInfo(dispenserId, model));
    break;

case "EptInfo":
    var eptTerminalId = message.Data?["TerminalId"] ?? "";
    var version = message.Data?["Version"] ?? "";
    _eventListener?.OnEptInfoReceived(new EptTerminalInfo(eptTerminalId, version));
    break;
```

### 3.5 BNA Report Buffer Entity

For sites with BNA devices, cash reconciliation data must be persisted locally and synced to cloud.

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Buffer/Entities/BufferedBnaReport.cs`

```csharp
public class BufferedBnaReport
{
    public int Id { get; set; }
    public string TerminalId { get; set; } = "";
    public int NotesAccepted { get; set; }
    public DateTimeOffset ReportedAtUtc { get; set; }
    public bool IsSynced { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 3.6 Implementation Steps

| Step | File(s) | Description |
|------|---------|-------------|
| 1 | `IFccPeripheralMonitor.cs`, `PeripheralModels.cs` | Interface and models |
| 2 | `IFccEventListener.cs` | Add 3 peripheral event callbacks |
| 3 | `DomsJplAdapter.cs` | Handle peripheral unsolicited messages |
| 4 | `BufferedBnaReport.cs` | EF entity for BNA persistence |
| 5 | `AgentDbContext.cs` | Add `DbSet<BufferedBnaReport>` |
| 6 | `CloudUploadWorker.cs` | Include BNA reports in cloud upload batches |
| 7 | Kotlin equivalents | Mirror in edge-agent |
| 8 | Tests | Unit tests for unsolicited message parsing |

### 3.7 Test Plan

- **Unit:** Unsolicited `EptBnaReport` message parsed correctly
- **Unit:** Unsolicited `DispenserInstallData` parsed correctly
- **Unit:** Unsolicited `EptInfo` parsed correctly
- **Integration:** VirtualLab pushes peripheral messages, adapter captures them
- **Unit:** `BufferedBnaReport` persisted to SQLite and included in cloud upload

---

## Phase 4 — Enhanced Pump Status

**Gap addressed:** G-04 (FpStatus supplemental data), G-10 (FpStatus sub-variants)

### 4.1 Extended Pump Status Model

The current `PumpStatus` record only carries basic fields. Add supplemental data as an optional extension.

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/PumpStatusExtended.cs`

```csharp
/// <summary>
/// Extended pump status data from FpStatus_3 supplemental parameters.
/// Ported from legacy FpStatusResponse.SupplementalStatus (16 parameter IDs).
/// </summary>
public record PumpStatusSupplemental
{
    // Param 04: Available storage modules
    public IReadOnlyList<int>? AvailableStorageModules { get; init; }

    // Param 05: Available fuel grades
    public IReadOnlyList<int>? AvailableGrades { get; init; }

    // Param 06: Grade option number
    public int? GradeOptionNo { get; init; }

    // Param 07-08: Extended fuelling data
    public long? FuellingVolumeExtended { get; init; }
    public long? FuellingMoneyExtended { get; init; }

    // Param 09: Attendant account ID
    public string? AttendantAccountId { get; init; }

    // Param 10: Blocking status
    public string? BlockingStatus { get; init; }

    // Param 11: Nozzle details (full)
    public NozzleDetail? NozzleDetail { get; init; }

    // Param 12: Operation mode number
    public int? OperationModeNo { get; init; }

    // Param 13: Price group ID
    public int? PriceGroupId { get; init; }

    // Param 14: Nozzle tag reader ID
    public string? NozzleTagReaderId { get; init; }

    // Param 15: Alarm status
    public string? AlarmStatus { get; init; }

    // Param 16: Minimum preset values
    public IReadOnlyList<long>? MinPresetValues { get; init; }
}

public record NozzleDetail(int Id, string? AsciiCode, string? AsciiChar);
```

### 4.2 Extend `PumpStatus` Record

Add an optional `Supplemental` property to the existing `PumpStatus`:

```csharp
// In the existing PumpStatus record, add:
public PumpStatusSupplemental? Supplemental { get; init; }
```

### 4.3 Update `DomsPumpStatusParser`

Modify `ParseStatusResponse()` to extract supplemental parameters when present in the JPL response.

**Reference:** Legacy `FpStatusParser.cs` parses supplemental params by ID (01-16). The JPL protocol may include these as nested fields in the `FpStatus_resp` data map. Extract them if present:

```csharp
// In DomsPumpStatusParser.ParseStatusResponse():
var supplemental = new PumpStatusSupplemental
{
    AvailableGrades = ParseIntList(data, "FpAvailableGrades"),
    AttendantAccountId = data.GetValueOrDefault("AttendantAccountId"),
    BlockingStatus = data.GetValueOrDefault("FpBlockingStatus"),
    AlarmStatus = data.GetValueOrDefault("FpAlarmStatus"),
    GradeOptionNo = ParseIntOrNull(data, "FpGradeOptionNo"),
    OperationModeNo = ParseIntOrNull(data, "FpOperationModeNo"),
    PriceGroupId = ParseIntOrNull(data, "PgId"),
    NozzleTagReaderId = data.GetValueOrDefault("NozzleTagReaderId"),
    // ... remaining fields
};
```

**Important:** If the DOMS JPL firmware does not include supplemental fields in `FpStatus_resp`, we may need to request `FpStatus_req` with a `SubCode` of `"3"` to get the full response (matching legacy FpStatus_3 variant). Verify with the JPL protocol specification.

### 4.4 Implementation Steps

| Step | File(s) | Description |
|------|---------|-------------|
| 1 | `PumpStatusExtended.cs` | New model for supplemental data |
| 2 | `PumpStatus.cs` | Add optional `Supplemental` property |
| 3 | `DomsPumpStatusParser.cs` (C#) | Parse supplemental fields from JPL response |
| 4 | `DomsPumpStatusParser.kt` (Kotlin) | Mirror changes |
| 5 | Tests | Verify supplemental extraction with sample JPL responses |

### 4.5 Test Plan

- **Unit:** Parser extracts all 16 supplemental parameters when present
- **Unit:** Parser returns null supplemental when fields absent (backwards compatible)
- **Integration:** VirtualLab simulator returns supplemental status, adapter captures it

---

## Phase 5 — Transaction Enhancements

**Gaps addressed:** G-05 (Transaction info mask), G-06 (Unsupervised transactions), G-07 (FpTotals), G-08 (Transaction state mutation), G-15 (OdduSyncWorker)

### 5.1 Transaction Info Mask Decoding (G-05)

Add an optional `TransactionMetadata` to the canonical transaction that carries info mask flags.

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/TransactionMetadata.cs`

```csharp
/// <summary>
/// Additional metadata from the supervised transaction buffer.
/// Ported from legacy TransInfoMask 8-bit flags.
/// </summary>
public record TransactionInfoMask
{
    public bool IsStoredTransaction { get; init; }
    public bool IsErrorTransaction { get; init; }
    public bool ExceedsMinLimit { get; init; }
    public bool PrepayModeUsed { get; init; }
    public bool VolumeIncluded { get; init; }
    public bool FinalizeNotAllowed { get; init; }
    public bool MoneyDueIsNegative { get; init; }
    public bool MoneyDueIncluded { get; init; }
    public long? MoneyDue { get; init; }
    public int? TransSequenceNo { get; init; }
    public int? TransLockId { get; init; }
}
```

**Update `DomsSupParamParser.cs`**: After parsing the base transaction fields, check for `TransInfoMask` field in the data map. If present, decode the 8 bits:

```csharp
// In DomsSupParamParser.Parse():
int? infoMask = data.TryGetValue("TransInfoMask", out var maskStr)
    ? int.Parse(maskStr) : null;

if (infoMask.HasValue)
{
    dto.InfoMask = new TransactionInfoMask
    {
        IsStoredTransaction     = (infoMask.Value & 0x01) != 0,
        IsErrorTransaction      = (infoMask.Value & 0x02) != 0,
        ExceedsMinLimit         = (infoMask.Value & 0x04) != 0,
        PrepayModeUsed          = (infoMask.Value & 0x08) != 0,
        VolumeIncluded          = (infoMask.Value & 0x10) != 0,
        FinalizeNotAllowed      = (infoMask.Value & 0x20) != 0,
        MoneyDueIsNegative      = (infoMask.Value & 0x40) != 0,
        MoneyDueIncluded        = (infoMask.Value & 0x80) != 0,
    };
}
```

**Add to `DomsJplTransactionDto`:**
```csharp
public record DomsJplTransactionDto(
    // ... existing fields ...
    TransactionInfoMask? InfoMask = null  // NEW
);
```

### 5.2 Unsupervised Transaction Support (G-06)

In the legacy system, unsupervised transactions arrive on ports 5004/5005 as `FpUnsupervisedTransaction` messages. In the JPL protocol, these should arrive as either:
- A separate `FpUnsupTrans_read_req/resp` request-response cycle, OR
- Unsolicited `FpUnsupervisedTransaction` push messages

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/Protocol/DomsUnsupervisedTransactionHandler.cs`

```csharp
/// <summary>
/// Handles unsupervised transaction retrieval from DOMS.
/// Legacy equivalent: Port 5004/5005 DPP parsing of FpUnsupervisedTransaction.
/// </summary>
internal static class DomsUnsupervisedTransactionHandler
{
    /// <summary>
    /// Build request to read unsupervised transaction buffer.
    /// </summary>
    public static JplMessage BuildReadRequest(int fpId = 0);

    /// <summary>
    /// Parse unsupervised transactions from response.
    /// Fields: FpId, Vol, Money (same as legacy FpUnsupervisedTransaction).
    /// </summary>
    public static IReadOnlyList<DomsJplTransactionDto> ParseReadResponse(JplMessage response);
}
```

**Extend `DomsJplAdapter.FetchTransactionsAsync()`:**

After the supervised lock-read-clear cycle, also fetch unsupervised transactions:

```csharp
// In FetchTransactionsAsync, after supervised fetch:
var unsupRequest = DomsUnsupervisedTransactionHandler.BuildReadRequest();
var unsupResponse = await _tcpClient.SendAsync(unsupRequest, "FpUnsupTrans_read_resp", ct);
var unsupTransactions = DomsUnsupervisedTransactionHandler.ParseReadResponse(unsupResponse);
// Normalize and add to the batch
```

**Extend unsolicited handling** — add to `OnUnsolicitedMessage()`:
```csharp
case "FpUnsupervisedTransaction":
    // Parse and invoke eventListener.OnTransactionAvailable()
    break;
```

### 5.3 FpTotals Support (G-07)

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/PumpTotals.cs`

```csharp
/// <summary>
/// Cumulative pump totals for shift reconciliation.
/// Ported from legacy FpTotals: FpId, TotalVol, TotalMoney.
/// </summary>
public record PumpTotals(
    int FpId,
    long TotalVolumeMicrolitres,
    long TotalAmountMinorUnits,
    DateTimeOffset ObservedAtUtc);
```

**Add to `IFccAdapter` or a new optional interface:**

```csharp
/// <summary>
/// Optional interface for adapters that support pump totals queries.
/// </summary>
public interface IFccTotalsProvider
{
    Task<IReadOnlyList<PumpTotals>> GetPumpTotalsAsync(CancellationToken ct);
}
```

**File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/Protocol/DomsTotalsHandler.cs`

```csharp
internal static class DomsTotalsHandler
{
    // FpTotals_req / FpTotals_resp
    public static JplMessage BuildTotalsRequest(int fpId = 0);
    public static IReadOnlyList<PumpTotals> ParseTotalsResponse(
        JplMessage response, string currencyCode, int pumpNumberOffset);
}
```

### 5.4 Transaction State Mutation via WebSocket (G-08)

The legacy `TransactionService` handles Odoo POS mutations (OrderUUID, PaymentId, AddToCart, Discard) via WebSocket messages from the POS. In the new architecture, the `OdooWsMessageHandler` already handles WebSocket traffic from Odoo POS. Verify and extend it to support all legacy mutation types.

**Audit `OdooWsMessageHandler.cs` for these operations:**

| Legacy Operation | Legacy Method | Needed in OdooWsMessageHandler |
|-----------------|--------------|-------------------------------|
| Set order UUID | `UpdateOrderUuidAsync(txnId, uuid, orderId, state)` | Check if handled |
| Set payment ID | `UpdatePaymentIdAsync(txnId, paymentId)` | Check if handled |
| Add to cart | `UpdateAddToCartAsync(txnId, addToCart, paymentId)` | Check if handled |
| Discard/void | `UpdateIsDiscard(data)` | Check if handled |
| Generic update | `UpdateTransaction(txnId, fields)` | Check if handled |

**If any are missing, add to `OdooWsMessageHandler`:**

```csharp
// In HandleMessage switch:
case "update_order_uuid":
    await HandleUpdateOrderUuid(payload);
    break;
case "update_payment_id":
    await HandleUpdatePaymentId(payload);
    break;
case "update_add_to_cart":
    await HandleUpdateAddToCart(payload);
    break;
case "discard_transaction":
    await HandleDiscardTransaction(payload);
    break;
```

**Extend `BufferedTransaction` entity** with mutation fields:

```csharp
// Add to BufferedTransaction.cs:
public string? OrderUuid { get; set; }
public int? OdooOrderId { get; set; }
public string? PaymentId { get; set; }
public bool AddToCart { get; set; }
public bool IsDiscarded { get; set; }
```

### 5.5 Implementation Steps

| Step | File(s) | Description |
|------|---------|-------------|
| 1 | `TransactionMetadata.cs` | Info mask model |
| 2 | `DomsSupParamParser.cs` | Parse TransInfoMask bits |
| 3 | `DomsJplTransactionDto.cs` | Add InfoMask field |
| 4 | `DomsUnsupervisedTransactionHandler.cs` | Unsupervised transaction handler |
| 5 | `DomsJplAdapter.cs` | Fetch unsupervised after supervised in FetchTransactionsAsync |
| 6 | `PumpTotals.cs`, `IFccTotalsProvider.cs` | Totals model and interface |
| 7 | `DomsTotalsHandler.cs` | JPL handler for FpTotals |
| 8 | `DomsJplAdapter.cs` | Implement `IFccTotalsProvider` |
| 9 | `OdooWsMessageHandler.cs` | Verify/add transaction mutation handlers |
| 10 | `BufferedTransaction.cs` | Add OrderUuid, PaymentId, AddToCart, IsDiscarded |
| 11 | Kotlin equivalents | Mirror steps 1-8 in edge-agent |
| 12 | Tests | Full test suite for each sub-feature |

### 5.6 Test Plan

- **Unit:** Info mask 0xFF decodes all 8 flags correctly; 0x00 is all false
- **Unit:** Unsupervised transaction parser extracts FpId, Vol, Money
- **Unit:** `DomsTotalsHandler` builds correct request and parses response
- **Unit:** `OdooWsMessageHandler` processes update_order_uuid, discard messages
- **Integration:** VirtualLab injects unsupervised transactions, adapter fetches them
- **Regression:** Supervised transaction flow unchanged

---

## Phase 6 — State Mapping Alignment

**Gap addressed:** G-13

### 6.1 Fix Kotlin vs C# State Mapping Inconsistency

Currently the two platforms disagree:

| DOMS State | C# → Canonical | Kotlin → Canonical | Correct Mapping |
|-----------|---------------|-------------------|-----------------|
| FpStarted (5) | `Authorized` | `Dispensing` | **`Authorized`** — pump is authorized and starting but not yet dispensing fuel |
| FpLocked (9) | `Offline` | `Idle` | **`Offline`** — pump is locked and should not accept transactions |

**Fix in `DomsFpMainState.kt`:**

```kotlin
// Change:
FP_STARTED(5) to map to PumpState.AUTHORIZED  // was DISPENSING
FP_LOCKED(9) to map to PumpState.OFFLINE       // was IDLE
```

### 6.2 Add Missing Legacy States

The legacy has states like `Starting_paused`, `Starting_terminated`, `Fuelling_terminated`, `Unavailable`, `Unavailable_and_calling` which map to production scenarios. While the JPL protocol may not expose these exact sub-states, they might appear in the `FpSubStates` supplemental field. If encountered, map them:

| Legacy State | JPL SubState | Canonical Mapping |
|-------------|-------------|-------------------|
| Starting_paused | SubState in FpStarted | `Paused` |
| Starting_terminated | SubState in FpStarted | `Completed` |
| Fuelling_terminated | SubState in FpFuelling | `Completed` |
| Unavailable | FpInoperative or FpClosed | `Offline` |
| Unavailable_and_calling | SubState combination | `Calling` |

### 6.3 Implementation Steps

| Step | File(s) | Description |
|------|---------|-------------|
| 1 | `DomsFpMainState.kt` | Fix FpStarted → AUTHORIZED, FpLocked → OFFLINE |
| 2 | `DomsFpMainState.cs` | Verify C# mapping is correct (it is) |
| 3 | `DomsPumpStatusParser.cs` + `.kt` | Handle sub-state combinations if supplemental data available |
| 4 | Tests | Verify both platforms produce identical canonical states |

### 6.4 Test Plan

- **Unit:** Every `DomsFpMainState` value produces the same canonical state in C# and Kotlin
- **Contract test:** Add a shared JSON fixture with all 14 states and expected canonical outputs; both platforms must pass

---

## Phase 7 — VirtualLab Simulator Updates

The VirtualLab simulator must support all new features to enable testing.

### 7.1 Pump Control Simulation

**Update `DomsJplSimulatorService.cs`** to handle new JPL messages:

| Message | Simulator Behavior |
|---------|-------------------|
| `FpEmergencyStop_req` | Set pump state to `Error`, return success |
| `FpCancelEmergencyStop_req` | Set pump state to `Idle`, return success |
| `FpClose_req` | Set pump state to `Closed`, return success |
| `FpOpen_req` | Set pump state to `Idle`, return success |

### 7.2 Price Management Simulation

**Add to `DomsJplSimulatorState.cs`:**

```csharp
public class SimulatedPriceSet
{
    public string PriceSetId { get; set; } = "01";
    public Dictionary<string, long> GradePrices { get; set; } = new();
}
```

**Handle in `DomsJplSimulatorService.cs`:**

| Message | Simulator Behavior |
|---------|-------------------|
| `FcPriceSet_req` | Return current prices from state |
| `FcPriceUpdate_req` | Update stored prices, return success |

### 7.3 Peripheral Message Simulation

**Add management endpoints:**

| Endpoint | Purpose |
|----------|---------|
| `POST /api/doms-jpl/push-bna-report` | Simulate BNA report to connected clients |
| `POST /api/doms-jpl/push-dispenser-install` | Simulate dispenser install data |
| `POST /api/doms-jpl/push-ept-info` | Simulate EPT terminal info |

### 7.4 Totals Simulation

**Handle in `DomsJplSimulatorService.cs`:**

| Message | Simulator Behavior |
|---------|-------------------|
| `FpTotals_req` | Return accumulated totals from injected transactions |

### 7.5 Unsupervised Transactions

**Add state and endpoint:**

```csharp
// In DomsJplSimulatorState:
public List<SimulatedDomsTransaction> UnsupervisedTransactions { get; } = new();

// Handle FpUnsupTrans_read_req → return unsupervised buffer
```

**Management endpoint:**
| Endpoint | Purpose |
|----------|---------|
| `POST /api/doms-jpl/inject-unsupervised-transaction` | Add to unsupervised buffer |

### 7.6 Implementation Steps

| Step | File(s) | Description |
|------|---------|-------------|
| 1 | `DomsJplSimulatorService.cs` | Handle pump control JPL messages |
| 2 | `DomsJplSimulatorState.cs` | Add price set state |
| 3 | `DomsJplSimulatorService.cs` | Handle price management JPL messages |
| 4 | `DomsJplManagementEndpoints.cs` | Add peripheral push endpoints |
| 5 | `DomsJplSimulatorService.cs` | Push peripheral messages to clients |
| 6 | `DomsJplSimulatorService.cs` | Handle FpTotals_req |
| 7 | `DomsJplSimulatorState.cs` | Add unsupervised transaction buffer |
| 8 | `DomsJplSimulatorService.cs` | Handle FpUnsupTrans_read_req |
| 9 | `DomsJplManagementEndpoints.cs` | Add unsupervised inject endpoint |
| 10 | `DomsJplSimulatorE2ETests.cs` | E2E tests for all new simulator features |

---

## Phase 8 — Cloud Backend Enhancements

### 8.1 Accept New Data Types

The cloud `DomsCloudAdapter` and ingestion pipeline must accept the new data flowing from edge agents.

**Update `UploadRequest` / cloud contracts to include:**

| Data Type | Cloud Handling |
|-----------|---------------|
| BNA reports | New ingestion endpoint or batch field |
| Price snapshots | Store in site configuration history |
| Pump totals | Store for shift reconciliation reports |
| Pump block/unblock history | Store for audit/compliance |
| Transaction info mask flags | Include in transaction detail storage |

### 8.2 Cloud API Endpoints

| Endpoint | Purpose |
|----------|---------|
| `POST /api/v1/sites/{siteId}/bna-reports` | Receive BNA reports from edge |
| `GET /api/v1/sites/{siteId}/pump-totals` | Query pump totals for reconciliation |
| `GET /api/v1/sites/{siteId}/pump-control-history` | Audit trail of pump blocks |
| `POST /api/v1/sites/{siteId}/price-snapshots` | Receive price change events |

### 8.3 Portal Visibility

Ensure the cloud portal can display:
- Current fuel prices per site
- Pump block/unblock audit trail
- BNA cash reconciliation data
- Pump totals for shift reports

---

## Cross-Cutting Concerns

### Interface Evolution Strategy

All new interfaces (`IFccPumpControl`, `IFccPriceManagement`, `IFccPeripheralMonitor`, `IFccTotalsProvider`) are **optional**. Consuming code should check via `is` pattern:

```csharp
if (adapter is IFccPumpControl pumpControl)
{
    await pumpControl.EmergencyStopAsync(fpId, ct);
}
```

This ensures non-DOMS adapters (Petronite, Advatec, Radix) are unaffected.

### Configuration

Add to `edge-agent-config.schema.json` under `fcc` section:

```json
{
  "pumpControl": {
    "enabled": true,
    "transactionLimitsEnabled": true,
    "defaultMaxTransactionsPerPump": 50
  },
  "priceManagement": {
    "enabled": true,
    "syncToCloud": true
  },
  "peripheralMonitoring": {
    "bnaReportingEnabled": true,
    "dispenserInventoryEnabled": true
  },
  "totals": {
    "enabled": true,
    "pollIntervalSeconds": 300
  }
}
```

### Logging

All new operations must use structured logging with consistent event IDs:

| Event ID Range | Category |
|---------------|----------|
| 5000-5099 | Pump control operations |
| 5100-5199 | Price management |
| 5200-5299 | Peripheral events |
| 5300-5399 | Pump totals |
| 5400-5499 | Unsupervised transactions |

### Migration Path from Legacy

For sites currently running `DOMSRealImplementation/`:

1. Deploy new edge agent with all Phase 1-6 features
2. Run both systems in parallel for 1 shift (legacy writes to SQL Server, new writes to SQLite + cloud)
3. Compare transaction counts, pump states, and BNA reports between old and new
4. Cutover to new system once parity confirmed
5. Decommission legacy SQL Server middleware

---

## Implementation Priority & Timeline

| Phase | Priority | Dependencies | Status |
|-------|----------|-------------|--------|
| Phase 6 (State mapping fix) | P0 - Critical | None | **DONE** |
| Phase 1 (Pump control + limits) | P0 - Critical | Phase 6 | **DONE** |
| Phase 2 (Price management) | P1 - High | None | **DONE** |
| Phase 3 (Peripheral messages) | P1 - High | None | **DONE** |
| Phase 5 (Transaction enhancements) | P1 - High | None | **DONE** |
| Phase 4 (Enhanced pump status) | P2 - Medium | None | **DONE** |
| Phase 7 (VirtualLab updates) | P2 - Medium | Phases 1-5 | **DONE** |
| Phase 8 (Cloud backend) | P2 - Medium | Phases 1-5 | **DONE** |

**Executed order:** Phase 6 → Phase 1 → Phase 2 → Phase 3 → Phase 5 → Phase 4 → Phase 7 → Phase 8 (ALL COMPLETE)

---

## Verification Checklist

Before declaring feature parity, verify each legacy capability:

**Phase 6 — State Mapping (P0 DONE):**
- [x] FpStarted(5) maps to same canonical state on C# and Kotlin — both → `Authorized`
- [x] FpLocked(9) maps to same canonical state on C# and Kotlin — both → `Offline`
- [x] All 14 FpMainState codes produce identical results on both platforms

**Phase 1 — Pump Control & Limits (P0 DONE — code implemented, needs integration testing):**
- [x] `EmergencyBlock()` → `EmergencyStopAsync()` — implemented in `DomsJplAdapter` (C# + Kotlin)
- [x] `UnblockPump()` → `CancelEmergencyStopAsync()` — implemented in `DomsJplAdapter` (C# + Kotlin)
- [x] `SoftLock()` → `ClosePumpAsync()` — implemented in `DomsJplAdapter` (C# + Kotlin)
- [x] `Unlock()` → `OpenPumpAsync()` — implemented in `DomsJplAdapter` (C# + Kotlin)
- [x] `CheckAndApplyPumpLimitAsync()` → `PumpLimitEnforcer.EnforceLimitsAsync()` — implemented
- [x] `CheckAndApplyPumpLimitAsync_IsAllowed()` → `PumpLimitEnforcer.EnforceLimitsWithOverrideAsync()` — implemented
- [x] `FpLimitReset()` → `PumpLimitEnforcer.ResetLimitAsync()` — implemented
- [x] `InsertBlockUnblockHistory()` → `PumpBlockHistory` entity + EF config — implemented
- [x] `UpsertAttendantPumpCountAsync()` → `AttendantPumpCount` entity + `PumpLimitEnforcer.UpsertAttendantPumpCountAsync()` — implemented

**Phase 2 — Price Management (P1 DONE — code implemented, needs integration testing):**
- [x] `RequestFcPriceSet()` → `GetCurrentPricesAsync()` — implemented in `DomsJplAdapter` (C# + Kotlin)
- [x] `SendDynamicPriceUpdate()` → `UpdatePricesAsync()` — implemented in `DomsJplAdapter` (C# + Kotlin)
- [x] `ChangeFcPriceSet` peripheral → `OnPriceChanged()` event fires — unsolicited handler added (C# + Kotlin)

**Phase 3 — Peripheral Messages (P1 DONE — code implemented, needs integration testing):**
- [x] `EptBnaReport` peripheral → `OnBnaReport()` event fires — unsolicited handler added (C# + Kotlin)
- [x] `DispenserInstallData` peripheral → `OnDispenserInstallData()` event fires — unsolicited handler added (C# + Kotlin)
- [x] `EptInfo` peripheral → `OnEptInfoReceived()` event fires — unsolicited handler added (C# + Kotlin)

**Phase 5 — Transaction Enhancements (P1 DONE — code implemented, needs integration testing):**
- [x] TransInfoMask 8-bit decode → `TransactionInfoMask` populated — `DomsSupParamParser` updated (C# + Kotlin)
- [x] `FpUnsupervisedTransaction` → unsupervised transactions fetched — `DomsUnsupervisedTransactionHandler` created (C#)
- [x] `FpTotals` → `GetPumpTotalsAsync()` — implemented in `DomsJplAdapter` (C# + Kotlin)
- [x] `UpdateOrderUuidAsync()` → OdooWsMessageHandler handles — verified: handled via `HandleManagerUpdateAsync` → `ParseUpdateFields` → `order_uuid`
- [x] `UpdatePaymentIdAsync()` → OdooWsMessageHandler handles — verified: handled via `ParseUpdateFields` → `payment_id`
- [x] `UpdateAddToCartAsync()` → OdooWsMessageHandler handles — verified: handled via `ParseUpdateFields` → `add_to_cart`
- [x] `UpdateIsDiscard()` → OdooWsMessageHandler handles — verified: handled via `HandleManagerManualUpdateAsync` → `DiscardTransactionAsync`

**Phase 4 — Enhanced Pump Status (P2 DONE):**
- [x] FpStatus_3 supplemental params → `PumpStatusSupplemental` populated — C# + Kotlin parsers extract all 16 params
- [x] `PumpStatus.Supplemental` optional property added on both platforms
- [x] Backwards compatible — returns null when supplemental fields absent

**Phase 7 — VirtualLab Simulator (P2 DONE):**
- [x] Pump control messages: FpEmergencyStop_req, FpCancelEmergencyStop_req, FpClose_req, FpOpen_req
- [x] Price management: FcPriceSet_req, FcPriceUpdate_req + SimulatedPriceSet state
- [x] Peripheral push endpoints: /push-bna-report, /push-dispenser-install, /push-ept-info
- [x] Pump totals: FpTotals_req handler + /set-pump-totals management endpoint
- [x] Unsupervised transactions: FpUnsupTrans_read_req + /inject-unsupervised-transaction endpoint
- [x] Price management endpoint: /set-prices
- [x] E2E tests for all new simulator features

**Phase 8 — Cloud Backend (P2 DONE):**
- [x] Domain entities: BnaReportRecord, PumpTotalsRecord, PriceSnapshotRecord, PumpControlHistoryRecord
- [x] DbContext: DbSets + table configurations + tenant query filters
- [x] SiteDataController: POST+GET for BNA, totals, prices, pump control history
- [x] Cloud contracts: SiteDataContracts.cs in FccMiddleware.Contracts.SiteData
- [x] Edge upload models: BnaReportBatchUpload, PumpTotalsBatchUpload, PumpControlHistoryBatchUpload, PriceSnapshotBatchUpload
- [x] Config schema: pumpControl, priceManagement, peripheralMonitoring, totals sections added
