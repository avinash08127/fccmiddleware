# DOMS Gap Analysis: Legacy vs New Implementation

## Overview

This document compares the **legacy DOMS implementation** (`DOMSRealImplementation/`) with the **new DOMS Adapter** across the Desktop Edge Agent, Edge Agent (Kotlin), and Cloud Backend. The goal is to identify features present in the legacy system that are missing or incomplete in the new implementation.

---

## Architecture Comparison

| Aspect | Legacy (`DOMSRealImplementation/`) | New (Edge + Cloud Adapters) |
|---|---|---|
| Protocol | Raw DPP HEX over 6 TCP ports (5001-5006) | JPL (JSON over STX/ETX framing) on single TCP port + HTTP REST option |
| Persistence | SQL Server with stored procedures | EF Core / SQLite local buffer |
| WebSocket | Built-in WebSocket server for POS/UI clients | Separate OdooWebSocketServer |
| UI | Web dashboard (port 8080) + WinForms attendant monitor | Avalonia desktop UI (separate layer) |
| Message Classification | Solicited/Unsolicited with per-port routing | Request/response correlation + unsolicited event listener |
| Transaction Flow | Direct DB insert per event | Buffer -> Normalize -> Canonical -> Cloud upload |

---

## GAP SUMMARY

| # | Feature | Severity | Status in New | Notes |
|---|---------|----------|---------------|-------|
| G-01 | Peripheral device messages | **High** | Missing | EPT, BNA, Dispenser install data not handled |
| G-02 | Price management | **High** | Missing | No price set tracking or ChangeFcPriceSet support |
| G-03 | Attendant limit management | **High** | Missing | No pump-level transaction limits or block/unblock |
| G-04 | FpStatus supplemental data (FpStatus_3) | **Medium** | Partial | Only basic fields extracted; supplemental params lost |
| G-05 | Transaction info mask flags | **Medium** | Missing | 8-bit mask (StoredTrans, ErrorTrans, etc.) not decoded |
| G-06 | Unsupervised transaction support | **Medium** | Missing | Port 5004/5005 unsupervised messages not supported |
| G-07 | FpTotals (running totals) | **Medium** | Missing | TotalVol / TotalMoney not parsed or tracked |
| G-08 | Transaction state mutation (Odoo) | **Medium** | Missing | OrderUUID, PaymentId, AddToCart, Discard not in adapter |
| G-09 | Block/unblock history logging | **Medium** | Missing | No InsertBlockUnblockHistory equivalent |
| G-10 | FpStatus sub-variants | **Low** | Simplified | Legacy: FpStatus_0 through FpStatus_3; new: single FpStatus_resp |
| G-11 | WebSocket server for POS clients | **Low** | Different design | Handled by OdooWebSocketServer, not adapter-level |
| G-12 | Web dashboard | **Low** | Different design | Replaced by Avalonia desktop UI |
| G-13 | FpMainState coverage | **Low** | Different mapping | See detailed state mapping below |
| G-14 | Fallback Console (port 5003) | **Low** | Missing | Dedicated DPP fallback console not present |
| G-15 | OdduSyncWorker | **Low** | Missing | Periodic FpSupTransBufStatus sync worker |

---

## Detailed Gap Analysis

### G-01: Peripheral Device Messages (HIGH)

**Legacy:** Parses three categories of peripheral messages on port 5006:

| Message | Data Extracted | Use Case |
|---------|---------------|----------|
| `DispenserInstallData` | DispenserId, Model | Dispenser inventory/commissioning |
| `EptInfo` | TerminalId, Version | Payment terminal discovery/audit |
| `EptBnaReport` | TerminalId, NotesAccepted | Banknote Acceptor cash reconciliation |
| `ChangeFcPriceSet` | FcId, NewPrice | Price change events from forecourt controller |

**New:** No equivalent. The new adapter has no concept of peripheral messages. Any site using BNA (Banknote Acceptor) cash payment or needing dispenser audit data will not have these events captured.

**Impact:** Sites with BNA devices will lose cash reconciliation data. Dispenser install data used for commissioning workflows will not be available. EPT terminal info won't be tracked.

---

### G-02: Price Management (HIGH)

**Legacy:**
- `DomsPriceSet` model with `PriceSetId`, `PriceGroupIds`, `GradeIds`, `CurrentPrices` (dictionary grade -> price)
- `HandlePriceSetRequest()` in repository layer
- `ChangeFcPriceSet` peripheral message parsing
- Price change event notifications via WebSocket

**New:** No price management at all. The adapter does not track, store, or forward price data. Product code mapping exists for normalization, but actual fuel prices at the grade level are not managed.

**Impact:** Cloud portal and site operations will not have visibility into current pump pricing or price change history.

---

### G-03: Attendant Limit Management (HIGH)

**Legacy provides a full attendant workflow:**

| Capability | Legacy Method | New Equivalent |
|-----------|--------------|----------------|
| Transaction limit per pump | `GetTransactionLimitCountByFpId(fpId)` | None |
| Reset pump limit | `FpLimitReset(fpId)` / `FpLimitReset(fpId, newLimit)` | None |
| Block/unblock pump | `UpdateIsAllowedAsync(fpId, isAllowed)` | None |
| Block/unblock history | `InsertBlockUnblockHistory(fpId, actionType, source, note)` | None |
| Attendant pump count | `UpdateAttendantPumpCountAsync(dto)` / `UpsertAttendantPumpCountAsync(dto)` | None |
| Block-specific limit query | `GetTransactionLimitCountByFpId_Block(fpId)` | None |
| UI popup for monitoring | `PopupService` with `AttendantMonitorWindow` | None |

**New:** The new adapter captures `attendantId` as an optional field on transactions but provides no attendant-level pump management.

**Impact:** Sites relying on DOMS attendant-based pump blocking, transaction limits, or real-time attendant monitoring will lose this functionality entirely.

---

### G-04: FpStatus Supplemental Data (MEDIUM)

**Legacy** parses `FpStatus_3` with 16 supplemental parameter IDs:

| Param ID | Field | Present in New? |
|----------|-------|:---:|
| 01 | FpSubStates2 | No |
| 02 | FpSubStates3 | No |
| 03 | FpSubStates4 | No |
| 04 | FpAvailableSms (storage modules) | No |
| 05 | FpAvailableGrades | No |
| 06 | FpGradeOptionNo | No |
| 07 | FuellingDataVol_e | No |
| 08 | FuellingDataMon_e | No |
| 09 | AttendantAccountId | No |
| 10 | FpBlockingStatus | No |
| 11 | NozzleId (ID + ASCII code + char) | Partial (ID only) |
| 12 | FpOperationModeNo | No |
| 13 | PgId (price group) | No |
| 14 | NozzleTagReaderId | No |
| 15 | FpAlarmStatus | No |
| 16 | MinPresetValues | No |

**New** extracts only: `FpId`, `FpMainState`, `NozzleId`, `CurrentVolume`, `CurrentAmount`, `UnitPrice`.

**Impact:** Loss of detailed pump diagnostics, grade availability, alarm status, blocking status, operation mode, and preset values. This data is often critical for site operations dashboards and troubleshooting.

---

### G-05: Transaction Info Mask Flags (MEDIUM)

**Legacy** decodes an 8-bit `TransInfoMask` for supervised transactions:

| Bit | Flag | Purpose |
|-----|------|---------|
| 0 | StoredTrans | Transaction is stored (vs live) |
| 1 | ErrorTrans | Transaction has error |
| 2 | TransGreaterThanMinLimit | Amount exceeds minimum |
| 3 | PrepayModeUsed | Prepay mode was used |
| 4 | VolOrVolAndGradeIdIncluded | Volume data presence |
| 5 | FinalizeNotAllowed | Cannot finalize |
| 6 | MoneyDueIsNegative | Negative balance |
| 7 | MoneyDueIncluded | MoneyDue field present |

**New:** Not decoded. The lock-read-clear protocol retrieves transactions but does not parse the info mask.

**Impact:** Loss of transaction metadata that can indicate error conditions, prepay mode usage, and finalization restrictions. May affect POS reconciliation workflows.

---

### G-06: Unsupervised Transaction Support (MEDIUM)

**Legacy:** Dedicated `ParseUnsupervised()` method processes `FpUnsupervisedTransaction` messages arriving on ports 5004/5005. These represent self-service transactions that bypass the supervised buffer.

**New:** Only supervised transactions via the lock-read-clear protocol. No unsupervised message handling.

**Impact:** Self-service pumps (card-based, app-based) that generate unsupervised transactions will not be captured.

---

### G-07: FpTotals (MEDIUM)

**Legacy:** Parses `FpTotals` messages (EXTC 0x09) with `TotalVol` and `TotalMoney` per pump. Used for shift reconciliation and audit trails.

**New:** No totals parsing or tracking.

**Impact:** Shift-end reconciliation and audit workflows that depend on cumulative pump totals will not have this data.

---

### G-08: Transaction State Mutation (MEDIUM)

**Legacy supports mid-lifecycle transaction mutations via WebSocket/API:**

| Operation | Method | Purpose |
|-----------|--------|---------|
| Set Odoo order UUID | `UpdateOrderUuidAsync(txnId, uuid, orderId, state)` | Link DOMS txn to Odoo order |
| Set payment ID | `UpdatePaymentIdAsync(txnId, paymentId)` | Link to payment gateway |
| Add to cart | `UpdateAddToCartAsync(txnId, addToCart, paymentId)` | POS cart integration |
| Discard/void | `UpdateIsDiscard(data)` | Void a transaction |
| Generic update | `UpdateTransaction(txnId, fields)` | Flexible field update |

**New:** The adapter normalizes and buffers transactions but does not support in-flight mutation. Once buffered, transactions flow one-way to the cloud. Odoo integration is handled by a separate `OdooWebSocketServer` / `OdooWsMessageHandler` rather than within the DOMS adapter.

**Impact:** The Odoo POS integration pattern is architecturally different. The legacy approach mutated DOMS transactions directly; the new approach separates concerns. Verify that the new OdooWsMessageHandler covers all required Odoo linking use cases (order UUID, payment ID, cart status).

---

### G-09: Block/Unblock History Logging (MEDIUM)

**Legacy:** `InsertBlockUnblockHistory(fpId, actionType, source, note)` maintains an audit trail of every pump block/unblock action with the actor and reason.

**New:** No equivalent. There is no concept of pump blocking in the new adapter, so there is no audit trail.

**Impact:** Loss of compliance/audit trail for pump access control actions.

---

### G-10: FpStatus Sub-Variants (LOW)

**Legacy** handles four variants of FpStatus with increasing detail:
- `FpStatus_0` - Minimal (FpId + state)
- `FpStatus_1` - Adds nozzle + volume
- `FpStatus_2` - Adds money + grade
- `FpStatus_3` - Full supplemental data (16 parameters)

**New** handles a single `FpStatus_resp` JPL message. This is expected since JPL protocol consolidates status into one response format. However, the depth of data available may vary depending on the FCC firmware version.

---

### G-13: FpMainState Mapping Differences (LOW)

**Legacy states** (DPP protocol):

| State | Present in New? |
|-------|:---:|
| Unconfigured | No (mapped to FpInoperative) |
| Closed | Yes (FpClosed) |
| Idle | Yes (FpIdle) |
| Error | Yes (FpError) |
| Calling | Yes (FpCalling) |
| PreAuthorized | Yes (FpAuthorized) |
| Starting | Yes (FpStarted) |
| Starting_paused | No |
| Starting_terminated | No |
| Fuelling | Yes (FpFuelling) |
| Fuelling_paused | Yes (FpSuspended) |
| Fuelling_terminated | No |
| Unavailable | No (partially mapped to FpOffline) |
| Unavailable_and_calling | No |

**New states** not in legacy: `FpLocked(9)`, `FpEmergencyStop(11)`, `FpDisconnected(12)`

**Kotlin vs C# state mapping inconsistency:**

| State | C# Maps To | Kotlin Maps To |
|-------|-----------|---------------|
| FpStarted (5) | Authorized | Dispensing |
| FpLocked (9) | Offline | Idle |

This inconsistency between the two platforms could cause different pump status reporting for the same hardware state.

---

## Features in New NOT in Legacy (Improvements)

| Feature | Description |
|---------|-------------|
| HTTP REST mode | `DomsAdapter` supports modern REST API FCC systems |
| Pre-auth cancellation | Explicit `CancelPreAuth` / `deauthorize_Fp_req` |
| Timezone-aware timestamps | IANA timezone -> UTC conversion |
| Product code mapping | Configurable per-site product code lookup |
| Pump number offset | Configurable pump numbering offset |
| Canonical transaction format | Standardized microlitres / minor currency units |
| Dead connection detection | Heartbeat with exponential backoff (Kotlin) |
| Credential store | S-DSK-011 secure credential integration |
| Android network binding | WiFi-specific FCC traffic routing |
| Error classification | Recoverable (408/429/5xx) vs non-recoverable (401/403) |
| VirtualLab simulator | Full TCP/JPL protocol simulator for testing |
| Cloud-side validation | Field-level validation with error codes |

---

## Recommendations

### Must Address (P0)

1. **Attendant limits and pump blocking (G-03)**: If any deployment site uses DOMS attendant-based pump management, this is a production blocker. Determine whether this is handled elsewhere (e.g., Odoo POS) or needs to be added to the adapter.

2. **Peripheral messages (G-01)**: Confirm whether BNA (Banknote Acceptor) devices are used at any target site. If yes, the `EptBnaReport` must be captured for cash reconciliation.

3. **Price management (G-02)**: Confirm whether price change visibility is required in the cloud portal. If pricing is managed entirely in Odoo/POS, this may be acceptable. If cloud needs price audit data, this is a gap.

### Should Address (P1)

4. **Unsupervised transactions (G-06)**: Confirm whether any site has self-service pumps. If yes, these transactions will be silently lost.

5. **FpTotals (G-07)**: Determine if shift reconciliation depends on DOMS pump totals or uses Odoo POS totals instead.

6. **Transaction info mask (G-05)**: The prepay mode flag and error transaction flag may be needed for POS reconciliation. Verify with POS workflow requirements.

7. **FpStatus supplemental data (G-04)**: Determine which supplemental fields are actually consumed by downstream systems. At minimum, `FpBlockingStatus` and `FpAlarmStatus` seem operationally important.

8. **Fix Kotlin vs C# state mapping inconsistency (G-13)**: `FpStarted(5)` and `FpLocked(9)` map to different canonical states across platforms. Align to a single mapping.

### Can Defer (P2)

9. **Transaction state mutation (G-08)**: Verify the OdooWsMessageHandler covers order UUID / payment ID linking. The architectural separation of concerns is valid but needs functional parity confirmation.

10. **Block/unblock audit trail (G-09)**: If pump blocking is not in the new adapter, the audit trail is N/A. If blocking is added (P0), add the audit trail too.

11. **FpStatus sub-variants (G-10)**: Acceptable if JPL protocol provides equivalent depth. Verify with FCC firmware documentation.

12. **Fallback Console (G-14)**: Low priority unless legacy sites depend on this recovery mechanism.

13. **OdduSyncWorker (G-15)**: The periodic buffer status poll is replaced by event-driven unsolicited messages in JPL. Acceptable if unsolicited push is enabled.

---

## Protocol Difference Note

The legacy system uses the **raw DPP (Defined Protocol for Petroleum)** binary protocol over 6 dedicated TCP ports, each carrying a specific message category. The new system uses **JPL (JSON Protocol Layer)**, which is a higher-level abstraction that wraps DPP concepts in JSON messages over a single TCP connection with STX/ETX binary framing.

This is a **deliberate architectural upgrade**, not a gap. JPL provides:
- Structured JSON instead of raw hex parsing
- Single connection instead of 6 ports
- Request-response correlation
- Built-in heartbeat framing

However, some DPP-level detail (supplemental parameters, info masks, peripheral messages) may not be exposed through the JPL layer depending on the FCC firmware version. This should be confirmed with the DOMS/JPL protocol specification.
