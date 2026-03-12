# Edge Agent Logging & Debugging Plan

## Context

The Edge Agent is deployed on 2,000+ Android HHTs and Windows desktops across sub-Saharan Africa with unreliable internet. Today, debugging a remote device requires physical access (days + cost). The Android agent has **no persistent log files** (logcat only), a **stub DiagnosticsActivity**, and **no remote log retrieval**. The desktop agent is in much better shape (Serilog file sink + LogsPage UI). This plan brings the Android agent to parity and adds remote debugging capabilities across the stack.

---

## Current State Summary

| Capability | Cloud | Desktop Agent | Android Agent |
|---|---|---|---|
| Structured logging | Serilog CLEF/JSON | Serilog file sink | **None** (logcat only) |
| Persistent logs | CloudWatch | Rolling files (14d) | **None** |
| Crash capture | Bootstrap Log.Fatal | Bootstrap Log.Fatal | **None** |
| Diagnostics UI | Admin dashboard | LogsPage (auto-refresh) | **Stub** |
| Telemetry to cloud | N/A | 7 error counters + device stats | 7 error counters + device stats |
| Remote log retrieval | CloudWatch Logs Insights | **None** | **None** |
| Correlation ID flow | Per-request middleware | **Not propagated** | **Not propagated** |
| Remote log level control | appsettings | **None** | Config field exists, **unwired** |
| Global exception handler | **Missing** | Bootstrap only | **Missing** |

---

## Phased Implementation

### Phase 1: Android Persistent Structured Logging (CRITICAL)

**1A. Create StructuredFileLogger** `[M]`
- NEW: `src/edge-agent/.../logging/StructuredFileLogger.kt`
  - Writes JSONL to `context.filesDir/logs/edge-agent-YYYY-MM-DD.jsonl`
  - Schema: `{"ts":"ISO8601","lvl":"INFO","tag":"TAG","msg":"...","cid":"...","extra":{...}}`
  - Rolling: 1 file/day, max 5 files (~10 MB cap)
  - Async buffered I/O via `Dispatchers.IO` + `Mutex` (no blocking on pre-auth hot path)
  - Bridges to `android.util.Log` simultaneously (ADB debugging still works)
  - Flushes immediately on ERROR level
- NEW: `src/edge-agent/.../logging/LogLevel.kt` - enum parsed from config
- MODIFY: `src/edge-agent/.../di/AppModule.kt` - register as Koin singleton

**1B. Migrate all Log.d/Log.i/Log.w/Log.e calls** `[M]`
- ~80+ call sites across all .kt files (adapters, sync workers, connectivity, buffer, API)
- Approach: create `logger.d(tag, msg)` wrapper -> find-replace `Log.d(TAG,` -> `logger.d(TAG,`

**1C. Global Uncaught Exception Handler** `[S]`
- MODIFY: `src/edge-agent/.../FccEdgeApplication.kt`
  - `Thread.setDefaultUncaughtExceptionHandler` -> write crash to StructuredFileLogger
  - `CoroutineExceptionHandler` on SupervisorJob scope in AppModule

### Phase 2: DiagnosticsActivity Implementation (HIGH)

**2A. Implement DiagnosticsActivity** `[M]`
- MODIFY: `src/edge-agent/.../ui/DiagnosticsActivity.kt` (currently stub)
- NEW: `src/edge-agent/.../res/layout/activity_diagnostics.xml`
- Sections: Connectivity state, Buffer stats, Sync status, Error counters, Device health, Recent audit logs (50 entries)
- Auto-refresh every 5s via Handler
- Reference: Desktop agent's `LogsPage.axaml.cs` pattern

**2B. Log Export (Share Logs)** `[S]`
- Add "Share Logs" button -> zips JSONL files -> Android share sheet (email/WhatsApp)
- NEW: `src/edge-agent/.../res/xml/file_provider_paths.xml`
- MODIFY: `AndroidManifest.xml` (register FileProvider)

### Phase 3: Remote Log Retrieval via Cloud (HIGH)

**3A. Cloud: Diagnostic Log Upload Endpoint** `[M]`
- NEW: `POST /api/v1/agent/diagnostic-logs` on AgentController
- NEW: `DiagnosticLogUploadRequest` contract (max 200 entries, WARN/ERROR only)
- NEW: `agent_diagnostic_logs` table (device_id, uploaded_at, log_json JSONB, 7-day retention)
- Follows existing telemetry upload pattern exactly

**3B. Android: Diagnostic Log Upload Worker** `[M]`
- MODIFY: `CloudUploadWorker.kt` - add `reportDiagnosticLogs()` on telemetry cadence
- Only uploads when `TelemetryDto.includeDiagnosticsLogs == true` (config field already exists)
- Reads recent WARN/ERROR from StructuredFileLogger, size-capped

**3C. Portal: Agent Log Viewer** `[M]`
- NEW: `GET /api/v1/agents/{id}/diagnostic-logs` on AgentsController
- MODIFY: Portal agent-detail component - add "Diagnostic Logs" tab

### Phase 4: Remote Log Level Control (MEDIUM) `[S]`

- MODIFY: `StructuredFileLogger.kt` - observe `ConfigManager.config` flow, react to `telemetry.logLevel` changes
- Config field already exists and is distributed via config poll - just needs wiring
- Portal: add per-agent log level override in admin settings

### Phase 5: End-to-End Correlation IDs (MEDIUM) `[M]`

- MODIFY: `CloudApiClient.kt` - add `X-Correlation-Id` header to all outbound requests
- MODIFY: Ktor local API - read `X-Correlation-Id` from incoming pre-auth requests, propagate
- MODIFY: `StructuredFileLogger.kt` - support coroutine-context correlation ID auto-attachment
- Cloud already reads `X-Correlation-Id` via CorrelationIdMiddleware

### Phase 6: Cloud Backend Hardening (MEDIUM)

**6A. Global Exception Handler Middleware** `[S]`
- NEW: `GlobalExceptionHandlerMiddleware.cs` - catches unhandled exceptions, logs structured, returns ErrorResponse
- MODIFY: `Program.cs` - register after CorrelationIdMiddleware

**6B. Circuit Breaker for External Calls** `[M]`
- Use Polly `CircuitBreakerAsync` on Odoo/Databricks HttpClient registrations
- Android agent already has this pattern (`CircuitBreaker.kt`)

### Phase 7: Portal Logging (LOW) `[S]`

- NEW: `LoggingService` Angular injectable (structured console + optional backend POST)
- MODIFY: `api.interceptor.ts` - replace `console.error` with LoggingService

---

## Implementation Order

| # | Item | Depends On | Size | Status |
|---|---|---|---|---|
| 1 | 1A: StructuredFileLogger | - | M | DONE |
| 2 | 1C: Crash Handler | 1A | S | DONE |
| 3 | 1B: Migrate Log calls | 1A | M | DONE (35 files) |
| 4 | 2A: DiagnosticsActivity | 1A | M | DONE |
| 5 | 2B: Log Export | 2A | S | DONE |
| 6 | 4: Wire logLevel config | 1A | S | DONE |
| 7 | 3A: Cloud Log endpoint | - | M | DONE |
| 8 | 3B: Android Log Upload | 1A, 3A | M | DONE |
| 9 | 3C: Portal Log Viewer | 3A | M | DONE |
| 10 | 5: Correlation IDs | 1A | M | DONE |
| 11 | 6A: Exception Handler | - | S | DONE |
| 12 | 6B: Circuit Breaker | - | M | DONE |
| 13 | 7: Portal Logging | - | S | DONE |

---

## Performance Guardrails

- StructuredFileLogger: async buffered I/O, never blocks pre-auth hot path
- Log file rotation on CleanupWorker cadence (reuse existing), not on every write
- Remote upload: max 200 entries, WARN/ERROR only, size-capped to minimize bandwidth
- Memory: <2 MB logging buffer (well within 180 MB budget)
- Total disk: 10 MB max log directory (safe for Urovo i9100)

## Verification

- **Phase 1**: Run Android agent -> reboot device -> verify logs persist in `filesDir/logs/`; trigger crash -> verify crash log written
- **Phase 2**: Open DiagnosticsActivity -> verify all sections populate; tap Share -> verify zip created
- **Phase 3**: Enable `includeDiagnosticsLogs` in config -> verify logs appear in portal agent detail
- **Phase 4**: Change `logLevel` to DEBUG via cloud config -> verify verbose logs appear on next cycle
- **Phase 5**: Make pre-auth request -> verify same correlationId appears in Android log + cloud log
- **Phase 6**: Trigger unhandled exception -> verify structured ErrorResponse returned + logged
