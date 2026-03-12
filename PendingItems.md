# Pending Items — Forecourt Middleware Platform

**Generated:** 2026-03-13
**Scope:** Full codebase scan for TODO, FIXME, HACK, PLACEHOLDER, STUB, not-yet-implemented, and unfinished work.

**Status Key:** `[code]` In-code marker · `[stub]` Stub / throws exception · `[placeholder]` Placeholder value · `[design]` Design-phase TODO (from TODO.md)

---

## 1. Cloud Backend (`src/cloud`)

### ~~1.1 OpenTelemetry Integration (CB-ServiceDefaults)~~ RESOLVED

OpenTelemetry packages added and wired into `AddServiceDefaults()`. Tracing (ASP.NET Core + HttpClient), metrics (ASP.NET Core + HttpClient + Runtime), OTLP export via `OpenTelemetry:OtlpEndpoint` config key.

### 1.2 Test Stubs (Expected — No Action Required)

| # | File | Line | Type | Description |
|---|------|------|------|-------------|
| 3 | `tests/FccMiddleware.IntegrationTests/PreAuth/PreAuthExpiryWorkerTests.cs` | 183, 185 | `[stub]` | `NoOpAdapter` throws `NotSupportedException` — intentional test double, not pending work |

---

## 2. Android Edge Agent (`src/edge-agent`)

### ~~2.1 DOMS Adapter — Not Yet Implemented (EA-1.x)~~ RESOLVED

The DOMS adapter **is fully implemented** via `DomsJplAdapter.kt` (TCP/JPL protocol, 15 source files).
The old `DomsAdapter.kt` is an intentional REST stub kept alongside the production TCP adapter.
The factory routes `FccVendor.DOMS` to `DomsJplAdapter` — no action needed.

| # | File | Status | Notes |
|---|------|--------|-------|
| — | `adapter/doms/DomsJplAdapter.kt` | **Complete** | Full TCP/JPL implementation with `IFccAdapter` + `IFccConnectionLifecycle` |
| — | `adapter/doms/jpl/` (4 files) | **Complete** | `JplFrameCodec`, `JplTcpClient`, `JplHeartbeatManager`, `JplMessage` |
| — | `adapter/doms/protocol/` (5 files) | **Complete** | Logon, PreAuth, Transaction, PumpStatus, SupParam handlers |
| — | `adapter/doms/model/` (3 files) | **Complete** | `DomsTransactionDto`, `DomsSupParam`, `DomsFpMainState` (14 states) |
| — | `adapter/doms/mapping/` (1 file) | **Complete** | `DomsCanonicalMapper` (integer-only arithmetic) |
| — | `adapter/doms/DomsAdapter.kt` | Stub (by design) | REST stub kept for potential VirtualLab REST simulator path |

### 2.2 Pre-Auth Cancel Method (EA-3.x)

| # | File | Line | Type | Description |
|---|------|------|------|-------------|
| 9 | `preauth/PreAuthHandler.kt` | 293 | `[code]` | TODO: `IFccAdapter` should expose a dedicated `cancelPreAuth()` method |
| 10 | `preauth/PreAuthHandler.kt` | 307 | `[code]` | TODO: call `adapter.cancelPreAuth()` when the interface adds that method. Currently only DB record is updated; FCC pre-auth expires naturally |
| 11 | `preauth/PreAuthHandler.kt` | 355 | `[code]` | TODO: replace `sendPreAuth(amount=0)` with dedicated `adapter.cancelPreAuth()` |

### 2.3 UI / Diagnostics (EA-3.x)

| # | File | Line | Type | Description |
|---|------|------|------|-------------|
| 12 | `ui/DiagnosticsActivity.kt` | 18 | `[code]` | TODO: Inflate diagnostics layout — entire UI is a stub |

### 2.4 Config Version Wiring (EA-2.x)

| # | File | Line | Type | Description |
|---|------|------|------|-------------|
| 13 | `api/StatusRoutes.kt` | 69 | `[code]` | TODO: Wire `ConfigManager` version into status response (currently `null`) |

---

## 3. Desktop Edge Agent (`src/desktop-edge-agent`)

### 3.1 Placeholder URLs / Values

| # | File | Line | Type | Description |
|---|------|------|------|-------------|
| 14 | `FccDesktopAgent.App/Services/VelopackUpdateService.cs` | 32 | `[placeholder]` | Velopack `UpdateManager` uses `"https://placeholder"` URL — real update feed URL not configured |
| 15 | `FccDesktopAgent.Api/Endpoints/StatusEndpoints.cs` | 21 | `[placeholder]` | Status endpoint returns placeholder values; needs `IConnectivityMonitor` + buffer stats (DEA-2.x) |
| 16 | `FccDesktopAgent.Core/Runtime/CadenceController.cs` | 177 | `[placeholder]` | FCC poll tick logs "ingestion orchestrator not registered (placeholder)" when not wired |
| 17 | `FccDesktopAgent.Core/Runtime/CadenceController.cs` | 204 | `[placeholder]` | Cloud upload tick logs "cloud sync service not registered (placeholder)" when not wired |
| 18 | `FccDesktopAgent.Core/Runtime/CadenceController.cs` | 228 | `[placeholder]` | SYNCED_TO_ODOO poll tick logs "poller not registered (placeholder)" when not wired |
| 19 | `FccDesktopAgent.Core/Runtime/CadenceController.cs` | 252 | `[placeholder]` | Config poll tick logs "config poller not registered (placeholder)" when not wired |
| 20 | `FccDesktopAgent.Core/Runtime/CadenceController.cs` | 276 | `[placeholder]` | Telemetry report tick logs "reporter not registered (placeholder)" when not wired |

### 3.2 Not-Yet-Implemented Features

| # | File | Line | Type | Description |
|---|------|------|------|-------------|
| 21 | `FccDesktopAgent.App/App.axaml.cs` | 128 | `[code]` | Agent restart via tray icon — "not yet implemented (DEA-2.x)" |
| 22 | `FccDesktopAgent.Api/Endpoints/PumpStatusEndpoints.cs` | 33 | `[stub]` | Pump status endpoints return `NOT_IMPLEMENTED` error response |
| 23 | `FccDesktopAgent.Core/Sync/CloudUploadWorker.cs` | 172 | `[code]` | TODO DEA-3.x: Surface `DEVICE_DECOMMISSIONED` to GUI alert / diagnostics dashboard |

### 3.3 Unsupported Adapters (By Design)

| # | File | Line | Type | Description |
|---|------|------|------|-------------|
| 24 | `FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs` | 51 | `[stub]` | Advatec adapter throws `NotSupportedException` — not supported on desktop |
| 25 | `FccDesktopAgent.Core/Config/DesktopFccRuntimeConfiguration.cs` | 70, 79 | `[stub]` | Unsupported FCC vendor throws `NotSupportedException` — validation guard |

---

## 4. Portal (`src/portal`)

### 4.1 Documentation Placeholders Only

| # | File | Line | Type | Description |
|---|------|------|------|-------------|
| 26 | `core/interceptors/auth.interceptor.ts` | 5 | `[placeholder]` | File is a documentation placeholder; actual auth handled by `MsalInterceptor` — no action needed |

**No TODO, FIXME, HACK, or stub code found in the portal.**

---

## 5. Infrastructure / CI (`infra/`, `.github/`)

**No TODO, FIXME, HACK, or stub markers found.**

---

## 6. High-Level Design TODOs (from `TODO.md`)

The root `TODO.md` contains a comprehensive pre-development checklist. Below are the **unchecked** categories still pending:

### Tier 1 — Foundational Contracts

| Area | Status | Notes |
|------|--------|-------|
| Shared enums documentation | Unchecked | Enums exist in code but formal documentation items remain open |
| Field-level FCC → canonical mapping doc | Unchecked | DOMS adapter reference mapping |
| Model versioning strategy | Unchecked | How to evolve without breaking field agents |
| State machine formal definitions | All unchecked | Transaction, pre-auth, edge sync, connectivity, reconciliation lifecycle |
| API contract specifications (OpenAPI) | All unchecked | 15+ endpoint specs not formally documented |
| Database schema design docs | All unchecked | Cloud (PostgreSQL) and Edge (SQLite/Room) |
| FCC adapter interface contracts doc | All unchecked | .NET and Kotlin interfaces |

### Tier 2 — Detailed Design Decisions

| Area | Status |
|------|--------|
| Error handling strategy | Unchecked |
| Deduplication strategy (detailed) | Unchecked |
| Reconciliation rules engine design | Unchecked |
| Configuration schema (full) | Unchecked |
| Security implementation plan | Unchecked |
| Event schema design | Unchecked |

### Tier 3 — Engineering Practices

| Area | Status |
|------|--------|
| Project scaffolding | Unchecked (but code exists — likely done, checkboxes not updated) |
| Repository & branching strategy | Unchecked |
| CI/CD pipeline design | Unchecked |
| Testing strategy | Unchecked |
| Observability & monitoring design | Unchecked |
| Coding conventions | Unchecked |

### Tier 4 — PoC & Validation

| Area | Status |
|------|--------|
| DOMS FCC protocol PoC | Unchecked |
| Urovo i9100 hardware validation | Unchecked |
| Edge agent background execution PoC | Unchecked |
| .NET MAUI evaluation | Unchecked |

### Tier 5 — Phased Development Plan

All phases (0–6) remain unchecked in the planning document.

---

## Summary by Priority

### Critical — Blocks Functionality
| # | Component | Item | Version Tag |
|---|-----------|------|-------------|
| ~~4–8~~ | ~~Edge Agent~~ | ~~DOMS adapter entirely stubbed~~ — **RESOLVED**: full TCP/JPL impl in `DomsJplAdapter.kt` | ~~EA-1.x~~ |
| 14 | Desktop Agent | Velopack update URL is `"https://placeholder"` | — |
| 22 | Desktop Agent | Pump status endpoints return NOT_IMPLEMENTED | — |

### High — Feature Gaps
| # | Component | Item | Version Tag |
|---|-----------|------|-------------|
| 9–11 | Edge Agent | Pre-auth cancel sends `amount=0` instead of proper cancel | EA-3.x |
| 12 | Edge Agent | Diagnostics UI is an empty stub | EA-3.x |
| 13 | Edge Agent | Status response missing config version | EA-2.x |
| 15 | Desktop Agent | Status endpoint returns placeholder data | DEA-2.x |
| 21 | Desktop Agent | Tray icon restart not implemented | DEA-2.x |
| 23 | Desktop Agent | Decommission alert not surfaced to GUI | DEA-3.x |

### Medium — Observability / Infrastructure
| # | Component | Item | Version Tag |
|---|-----------|------|-------------|
| ~~1–2~~ | ~~Cloud~~ | ~~OpenTelemetry not yet wired~~ — **RESOLVED** | ~~CB-ServiceDefaults~~ |
| 16–20 | Desktop Agent | CadenceController placeholder logs when services not registered | — |

### Low / By Design — No Immediate Action
| # | Component | Item | Reason |
|---|-----------|------|--------|
| 3 | Cloud | Test `NoOpAdapter` throws | Intentional test stub |
| 4–8 | Edge Agent | `DomsAdapter.kt` REST stub throws | By design — production uses `DomsJplAdapter.kt` (TCP/JPL) |
| 24–25 | Desktop Agent | Advatec / unsupported vendor throws | Design-time validation |
| 26 | Portal | Auth interceptor placeholder file | Documentation only |

---

*Total in-code markers: **26**
Resolved: **7** (DOMS adapter items 4–8, OpenTelemetry items 1–2)
Actionable items: **12** (excluding resolved, test stubs, design-time guards, and documentation placeholders)*
