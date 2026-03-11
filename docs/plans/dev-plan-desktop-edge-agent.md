# Desktop Edge Agent — Phased Development Plan

**Agent System Prompt:** Always prepend `docs/plans/agent-prompt-desktop-edge-agent.md` when assigning any task below.

**Sprint Cadence:** 2-week sprints

## Technology Decisions

| Component | Choice | Rationale |
|-----------|--------|-----------|
| Runtime | .NET 10 (LTS track) | Matches cloud backend version; cross-platform; high performance |
| GUI Framework | Avalonia UI 11 | Most mature cross-platform .NET desktop UI; Skia-based rendering; MVVM; supports Windows/macOS/Linux; NativeAOT-compatible |
| Local REST API | ASP.NET Core (Kestrel) | Embedded lightweight HTTP server; same tech as cloud backend |
| Database | EF Core 10 + SQLite | Cross-platform; WAL mode; migration support; familiar to .NET developers |
| HTTP Client | IHttpClientFactory + HttpClient | Built-in; resilient; supports DelegatingHandler for auth/retry |
| DI | Microsoft.Extensions.DependencyInjection | Built-in; same as cloud backend |
| Background Workers | IHostedService / BackgroundService | Built-in; lifecycle-managed by the Generic Host |
| Installer/Updater | Velopack | Cross-platform auto-update (Windows/macOS/Linux); delta updates; lightweight |
| Credential Storage | Platform-specific via unified abstraction: DPAPI (Windows), Keychain (macOS), libsecret (Linux) |
| Logging | Serilog + Microsoft.Extensions.Logging | Structured logging; file + console sinks; sensitive-field redaction |
| Serialization | System.Text.Json | Built-in; AOT-friendly; fast |
| Testing | xUnit + NSubstitute + FluentAssertions | Standard .NET test stack |

### Minimum OS Versions

| Platform | Minimum Version |
|----------|----------------|
| Windows | Windows 10 (1809+) |
| macOS | macOS 12 (Monterey) |
| Linux | Ubuntu 22.04 / Fedora 38 / Debian 12 or equivalent |

### Target Binary Size

| Publish Mode | Estimated Size |
|-------------|---------------|
| Self-contained + trimmed + ReadyToRun | 40–60 MB per platform |
| Framework-dependent (requires .NET runtime) | 8–15 MB |
| NativeAOT (if Avalonia supports fully) | 25–40 MB |

Default distribution: **self-contained single-file** so no runtime pre-installation is required at site.

## Performance Guardrails

These budgets are design constraints for the Desktop Edge Agent and should be validated throughout development, not only during hardening.

- `POST /api/preauth` p95 local API overhead: <= 50 ms before FCC call time (desktop is faster than HHT)
- `POST /api/preauth` p95 end-to-end on healthy FCC LAN: <= 1.5 s; p99 <= 3 s
- `GET /api/transactions` p95 for first page (`limit <= 50`) with 30,000 buffered records: <= 100 ms
- `GET /api/status` p95: <= 50 ms
- `GET /api/pump-status` live-response target on healthy LAN: <= 1 s; stale fallback response: <= 50 ms
- Steady-state Desktop Agent RSS target: <= 250 MB during normal operation
- 30,000-record local buffer target must be supported without degradation
- Replay throughput target on stable internet: >= 600 transactions/minute while preserving chronological ordering
- Cold start to API-ready: <= 5 seconds on typical hardware
- Installer size target: <= 60 MB (self-contained single-file)

---

## Phase 0 — Foundations (Sprints 1–2)

### DEA-0.0: Performance Budgets & Benchmark Harness

**Sprint:** 1
**Prereqs:** None
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `Requirements.md` — REQ-15 (Edge Agent), Non-Functional Requirements
- `HighLevelRequirements.md` — §15 (Edge Android Agent)
- `WIP-HLD-Edge-Agent.md` — performance, reliability, and deployment sections

**Task:**
Define and automate the core performance guardrails for the Desktop Edge Agent before feature development expands.

**Detailed instructions:**
1. Convert the guardrails above into measurable benchmark and test cases
2. Add lightweight benchmark harnesses using BenchmarkDotNet for:
    - Local API latency (Kestrel request pipeline)
    - EF Core + SQLite query latency on 30,000 buffered transactions
    - Replay throughput against mock cloud responses
    - Memory footprint during steady-state polling and sync
3. Add synthetic seed generation for representative 30,000-transaction datasets
4. Document pass/fail thresholds so later tasks can reference them explicitly

**Acceptance criteria:**
- Guardrail thresholds are documented in the repo and referenced by later Desktop Edge Agent tasks
- Benchmark harness can exercise a 30,000-record local dataset
- Pre-auth, local API, replay, and memory test paths are measurable before Phase 2 work begins
- At least one automated benchmark check is ready to run in CI or locally during development

---

### DEA-0.1: .NET Project Scaffold

**Sprint:** 1
**Prereqs:** None
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-1-project-scaffolding.md` — adapt the Edge Agent section for .NET
- `docs/specs/foundation/coding-conventions.md` — apply .NET conventions

**Task:**
Create the complete .NET solution for the Desktop Edge Agent.

**Detailed instructions:**
1. Create solution at `src/desktop-edge-agent/FccDesktopAgent.sln`
2. Create the following projects:

   ```
   src/desktop-edge-agent/
   ├── FccDesktopAgent.sln
   ├── src/
   │   ├── FccDesktopAgent.Core/              # Class library — domain models, interfaces, business logic
   │   │   ├── Adapter/
   │   │   │   ├── Common/                    # IFccAdapter interface, shared types, enums
   │   │   │   └── Doms/                      # DOMS adapter implementation
   │   │   ├── Buffer/                        # EF Core DbContext, entities, buffer manager
   │   │   ├── Connectivity/                  # Connectivity state machine
   │   │   ├── Ingestion/                     # Ingestion orchestrator, FCC poller
   │   │   ├── PreAuth/                       # Pre-auth handler
   │   │   ├── Sync/                          # Cloud upload, status poll, config poll, telemetry
   │   │   ├── Config/                        # Configuration manager
   │   │   ├── Security/                      # Credential storage abstraction, sensitive field handling
   │   │   └── Runtime/                       # Cadence controller
   │   ├── FccDesktopAgent.Api/               # Class library — Kestrel REST API controllers/endpoints
   │   ├── FccDesktopAgent.App/               # Avalonia UI executable — GUI shell, system tray, host bootstrap
   │   │   ├── Views/
   │   │   ├── ViewModels/
   │   │   ├── Assets/
   │   │   └── Program.cs
   │   └── FccDesktopAgent.Service/           # Console executable — headless service mode (Windows Service / systemd / launchd)
   └── tests/
       ├── FccDesktopAgent.Core.Tests/
       ├── FccDesktopAgent.Api.Tests/
       └── FccDesktopAgent.Integration.Tests/
   ```

3. Configure all projects:
   - `TargetFramework = net10.0`
   - Enable nullable reference types
   - Enable implicit usings
   - `FccDesktopAgent.App`: Avalonia UI 11 dependencies, AvaloniaUI NuGet packages
   - `FccDesktopAgent.Core`: EF Core 10 + Microsoft.Data.Sqlite, System.Text.Json, Serilog
   - `FccDesktopAgent.Api`: Microsoft.AspNetCore.App framework reference
   - `FccDesktopAgent.Service`: Microsoft.Extensions.Hosting, Microsoft.Extensions.Hosting.WindowsServices, Microsoft.Extensions.Hosting.Systemd
   - Test projects: xUnit, NSubstitute, FluentAssertions, Microsoft.EntityFrameworkCore.InMemory

4. Configure publish profiles:
   - `PublishSingleFile = true`
   - `SelfContained = true`
   - `PublishTrimmed = true` (with trimmer roots for Avalonia/EF Core reflection)
   - `RuntimeIdentifiers`: `win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`

5. Add `.editorconfig` with consistent code style rules
6. Add `Directory.Build.props` for shared version management

**Acceptance criteria:**
- `dotnet build` succeeds for all projects
- `dotnet publish -r win-x64 -c Release` produces a single-file executable
- All projects reference shared version props
- Solution compiles with zero warnings
- Test projects run with `dotnet test`

---

### DEA-0.2: Background Service Infrastructure

**Sprint:** 1
**Prereqs:** DEA-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `WIP-HLD-Edge-Agent.md` — §4.1 (service lifecycle), §8 (reliability)
- `docs/plans/dev-plan-edge-agent.md` — EA-0.2 (foreground service) for functional parity reference

**Task:**
Implement the background service host that keeps the Desktop Edge Agent running persistently, with both GUI and headless modes.

**Detailed instructions:**
1. Create `AgentHostBuilder` in `FccDesktopAgent.Core/Runtime/`:
   - Configure `IHostBuilder` with all background services
   - Register Kestrel web host for local API
   - Register all `IHostedService` implementations (stubs initially)
   - Configure Serilog with file rotation + console output
   - Configure graceful shutdown with `IHostApplicationLifetime`

2. **GUI mode** (`FccDesktopAgent.App`):
   - Avalonia `Program.cs` starts the .NET Generic Host alongside the Avalonia application
   - System tray icon with context menu: Show Dashboard, Restart Agent, Exit
   - Persistent system tray presence (minimize to tray, not taskbar)
   - Status indicator in tray icon (green/yellow/red based on connectivity)
   - Start the host on app launch; stop on app exit

3. **Headless service mode** (`FccDesktopAgent.Service`):
   - `Program.cs` uses `Host.CreateDefaultBuilder` with `.UseWindowsService()` and `.UseSystemd()`
   - Can be installed as Windows Service, systemd unit, or launchd plist
   - No GUI dependency — pure console/service host
   - Include install/uninstall scripts for each platform

4. Both modes share the same `AgentHostBuilder` — only the outer shell differs

5. Implement health check endpoint at `/health` for external monitoring

**Acceptance criteria:**
- GUI mode launches Avalonia window and starts all background services
- Headless mode runs as a console app and can be installed as a Windows Service
- System tray icon shows and responds to right-click menu
- Minimize to tray works (window hides, tray persists)
- Graceful shutdown stops all services cleanly
- Health endpoint returns 200 OK

---

### DEA-0.3: SQLite Database Setup (EF Core)

**Sprint:** 1
**Prereqs:** DEA-0.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.5 (Edge Agent Room/SQLite Schema — adapt for EF Core)
- `db/ddl/002-edge-room-schema.sql` — reference DDL

**Task:**
Implement the complete SQLite database with all entities, configurations, and migrations using EF Core.

**Detailed instructions:**
1. Create `AgentDbContext` extending `DbContext` in `Buffer/`:
   - SQLite provider with WAL journal mode: `PRAGMA journal_mode=WAL;`
   - Enable foreign keys: `PRAGMA foreign_keys=ON;`
   - Configure connection string to platform-appropriate app data directory:
     - Windows: `%LOCALAPPDATA%/FccDesktopAgent/`
     - macOS: `~/Library/Application Support/FccDesktopAgent/`
     - Linux: `~/.local/share/FccDesktopAgent/`

2. Create EF Core entities matching the Edge Agent schema:
   - `BufferedTransaction` — all columns from spec (Id, FccTransactionId, SiteCode, PumpNumber, NozzleNumber, ProductCode, VolumeMicrolitres, AmountMinorUnits, UnitPriceMinorPerLitre, CurrencyCode, StartedAt, CompletedAt, FiscalReceiptNumber, FccVendor, AttendantId, Status, SyncStatus, IngestionSource, RawPayloadJson, CorrelationId, UploadAttempts, LastUploadAttemptAt, LastUploadError, SchemaVersion, CreatedAt, UpdatedAt) — `PumpNumber`/`NozzleNumber` here are **FCC numbers** as received from the FCC
   - `PreAuthRecord` — all columns from spec
   - `NozzleMapping` — Id, SiteCode, OdooPumpNumber, FccPumpNumber, OdooNozzleNumber, FccNozzleNumber, ProductCode, IsActive, SyncedAt, CreatedAt, UpdatedAt
   - `SyncState` — single-row table (Id=1)
   - `AgentConfig` — single-row table (Id=1)
   - `AuditLog` — local audit trail

3. All timestamps as `DateTimeOffset` stored as ISO 8601 UTC text. Money as `long` (minor units). UUIDs as `string`.

4. Configure indexes via Fluent API matching spec:
   - `ix_bt_dedup`: `(FccTransactionId, SiteCode)` UNIQUE
   - `ix_bt_sync_status`: `(SyncStatus, CreatedAt)`
   - `ix_bt_local_api`: `(SyncStatus, PumpNumber, CompletedAt DESC)`
   - `ix_bt_cleanup`: `(SyncStatus, UpdatedAt)`
   - `ix_nozzles_odoo_lookup`: `(SiteCode, OdooPumpNumber, OdooNozzleNumber)` UNIQUE
   - `ix_nozzles_fcc_lookup`: `(SiteCode, FccPumpNumber, FccNozzleNumber)` UNIQUE
   - `ix_par_idemp`: `(OdooOrderId, SiteCode)` UNIQUE
   - `ix_par_unsent`: `(IsCloudSynced, CreatedAt)`
   - `ix_par_expiry`: `(Status, ExpiresAt)`
   - `ix_al_time`: `(CreatedAt)`

5. Configure `OnDelete` behaviors and unique constraint conflict handling
6. Add EF Core migrations support with initial migration
7. Enable WAL mode via connection interceptor or `OnConfiguring`
8. Set `OnConflict IGNORE` equivalent via SaveChanges exception handling for dedup-key inserts

9. Validate storage layout against hot-path performance:
   - Keep local API query columns on the hot table/index path
   - Evaluate whether raw payload storage should be isolated from the hottest query path
   - Benchmark query latency on a representative 30,000-record dataset

**Acceptance criteria:**
- EF Core migrations generate valid SQLite DDL
- Database file created in platform-appropriate location
- WAL mode enabled (verified via PRAGMA query)
- Unique indexes prevent duplicate inserts
- `GetPendingForUpload` returns records in `CreatedAt ASC` order
- `GetForLocalApi` excludes SYNCED_TO_ODOO records
- Local API query benchmarks remain within the documented guardrails on a representative backlog dataset
- In-memory SQLite tests pass for all query operations

---

### DEA-0.4: Domain Models & IFccAdapter Interface

**Sprint:** 1
**Prereqs:** DEA-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/canonical/canonical-transaction.schema.json` — transaction model
- `schemas/canonical/pre-auth-record.schema.json` — pre-auth model
- `schemas/canonical/pump-status.schema.json` — pump state model
- `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` — §5.1 adapter contract (adapt for C#)

**Task:**
Create C# domain models and the FCC adapter interface.

**Detailed instructions:**
1. Create `CanonicalTransaction` record in `Adapter/Common/` matching the JSON schema — use `System.Text.Json` serialization attributes
2. Create `PreAuthRecord` record (domain, not EF entity)
3. Create `PumpStatus` record
4. Create all shared enums matching the Cloud Backend:
   - `TransactionStatus`, `PreAuthStatus`, `SyncStatus` (Edge-only: Pending, Uploaded, SyncedToOdoo, Archived)
   - `IngestionMode`, `FccVendor`, `ConnectivityState`
5. Create `IFccAdapter` interface:
   - `Task<CanonicalTransaction> NormalizeAsync(RawPayloadEnvelope rawPayload, CancellationToken ct)`
   - `Task<PreAuthResult> SendPreAuthAsync(PreAuthCommand command, CancellationToken ct)`
   - `Task<IReadOnlyList<PumpStatus>> GetPumpStatusAsync(CancellationToken ct)`
   - `Task<bool> HeartbeatAsync(CancellationToken ct)`
   - `Task<TransactionBatch> FetchTransactionsAsync(FetchCursor cursor, CancellationToken ct)`
6. Create supporting types: `RawPayloadEnvelope`, `FetchCursor`, `TransactionBatch`, `PreAuthCommand`, `PreAuthResult`
7. Create `IFccAdapterFactory` interface: `IFccAdapter Create(FccVendor vendor, FccConnectionConfig config)`

**Acceptance criteria:**
- All models match their JSON schema counterparts field-for-field
- `IFccAdapter` interface matches adapter contracts spec (adapted for C# async patterns)
- All methods accept `CancellationToken`
- `SyncStatus` is separate from `TransactionStatus`
- Models use `long` for money, `DateTimeOffset` for timestamps, `string` for UUIDs

---

### DEA-0.5: Kestrel Local API Scaffold

**Sprint:** 2
**Prereqs:** DEA-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/edge-agent-local-api.yaml` — all 7 local API endpoints

**Task:**
Scaffold the Kestrel embedded HTTP server with stub routes.

**Detailed instructions:**
1. Create `LocalApiStartup` in `FccDesktopAgent.Api/`:
   - Configure Kestrel to listen on a configurable port (default `8585`)
   - Bind to `0.0.0.0` by default (desktop is always LAN-accessible — unlike Android which defaults to localhost)
   - Install System.Text.Json serialization
   - Install exception handling middleware
   - Install API key authentication middleware (all requests require API key — no localhost bypass since Odoo POS is always on a separate device)

2. Create Minimal API endpoint groups with placeholder 501 responses:
   - `TransactionEndpoints`:
     - `GET /api/transactions` — list buffered transactions
     - `GET /api/transactions/{id}` — get by ID
     - `POST /api/transactions/acknowledge` — Odoo POS acknowledges
   - `PreAuthEndpoints`:
     - `POST /api/preauth` — submit pre-auth
     - `POST /api/preauth/cancel` — cancel pre-auth
   - `PumpStatusEndpoints`:
     - `GET /api/pump-status` — live pump statuses
   - `StatusEndpoints`:
     - `GET /api/status` — agent status and connectivity

3. Register the Kestrel host as part of the Generic Host via `AgentHostBuilder`

**Acceptance criteria:**
- Kestrel server starts on port 8585
- All 7 endpoints return 501 (Not Implemented) with structured JSON
- `GET /api/status` returns 200 with placeholder status (this one should work)
- Requests without valid API key receive 401
- Content negotiation serializes/deserializes JSON correctly

---

### DEA-0.6: DI Setup

**Sprint:** 2
**Prereqs:** DEA-0.1, DEA-0.3, DEA-0.5
**Estimated effort:** 0.5 day

**Task:**
Configure dependency injection in the Generic Host.

**Detailed instructions:**
1. Create `ServiceCollectionExtensions` in `FccDesktopAgent.Core/`:
   - `AddAgentCore(this IServiceCollection services, IConfiguration config)` — registers all core services
   - `AddAgentDatabase(this IServiceCollection services, IConfiguration config)` — registers DbContext + migrations
   - `AddAgentApi(this IServiceCollection services)` — registers API controllers/services
   - `AddAgentBackgroundWorkers(this IServiceCollection services)` — registers all IHostedService workers

2. Register services:
   - `services.AddDbContext<AgentDbContext>(...)` — SQLite with WAL
   - `services.AddHttpClient(...)` — named clients for FCC and Cloud
   - `services.AddSingleton<IFccAdapterFactory, FccAdapterFactory>()`
   - `services.AddSingleton<ConnectivityManager>()`
   - `services.AddSingleton<ConfigManager>()`
   - Stubs for future services

3. Both `FccDesktopAgent.App` and `FccDesktopAgent.Service` call the same extension methods

**Acceptance criteria:**
- All services resolve without runtime errors on startup
- Database and API are injectable and start correctly
- No service locator anti-pattern — constructor injection only

---

### DEA-0.7: CI Pipeline Setup

**Sprint:** 2
**Prereqs:** DEA-0.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-3-2-repo-branching-and-cicd.md` — CI/CD spec

**Task:**
Create the CI pipeline for the Desktop Edge Agent.

**Detailed instructions:**
1. Create `.github/workflows/ci-desktop-agent.yml` with:
   - Trigger: push to `main`, PRs targeting `main` (path filter: `src/desktop-edge-agent/**`)
   - Matrix: `ubuntu-latest`, `windows-latest`, `macos-latest`
   - Steps: checkout → setup .NET 10 → restore → build → test → publish (single-file, self-contained)
2. Verify published artifact size is within budget
3. Add code style enforcement (dotnet format or equivalent)

**Acceptance criteria:**
- CI passes on all three OS platforms
- Published single-file executables produced as artifacts
- Unit tests run on all platforms
- Build size logged and compared to budget

---

### DEA-0.8: Installer & Auto-Update Infrastructure

**Sprint:** 2
**Prereqs:** DEA-0.1, DEA-0.2
**Estimated effort:** 2 days

**Task:**
Set up cross-platform installer packaging and auto-update mechanism using Velopack.

**Detailed instructions:**
1. Integrate Velopack into `FccDesktopAgent.App`:
   - Add `Velopack` NuGet package
   - Configure update source URL (cloud-hosted releases endpoint)
   - On app startup: check for updates, apply if auto-update enabled
   - Show update notification in UI (non-blocking)
   - Support manual update check from Settings screen
   - Support disabling auto-update via config

2. Configure platform-specific packaging:
   - **Windows**: Velopack produces a Setup.exe installer + delta update nupkgs
   - **macOS**: Velopack produces a .app bundle (optionally in .dmg)
   - **Linux**: Velopack produces AppImage

3. Add CI step to build installers:
   - `vpk pack` command in CI for each RID
   - Upload installer artifacts

4. Create update release workflow:
   - Build → Pack → Upload to releases endpoint (GitHub Releases or cloud storage)
   - Delta updates for bandwidth efficiency

**Acceptance criteria:**
- Windows Setup.exe installs and launches the app
- macOS .app bundle runs after extracting
- Linux AppImage runs on Ubuntu 22.04+
- Auto-update detects new version and applies it
- Manual update check works from UI
- Auto-update can be disabled via config
- Installer size within budget (≤ 60 MB)

---

## Phase 2 — Desktop Edge Agent Core (Sprints 4–7)

### DEA-2.1: DOMS FCC Adapter (LAN)

**Sprint:** 4
**Prereqs:** DEA-0.4
**Estimated effort:** 3–4 days

**Read these artifacts before starting:**
- `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` — §5.5 DOMS MVP Adapter Contract
- `schemas/canonical/canonical-transaction.schema.json` — normalization target
- `schemas/canonical/pump-status.schema.json` — pump status model
- `schemas/canonical/pre-auth-record.schema.json` — pre-auth command/result model

**Task:**
Implement the DOMS FCC adapter for LAN communication.

**Detailed instructions:**
1. Create `DomsFccAdapter` implementing `IFccAdapter` in `Adapter/Doms/`:
2. **`FetchTransactionsAsync(cursor)`**:
   - HTTP GET to `http://{hostAddress}:{port}/api/v1/transactions?since={ISO8601}&cursor={token}&limit={n}`
   - Auth: `X-API-Key` header from config
   - Parse JSON response containing `transactions[]`, `nextCursor`, `hasMore`
   - Normalize each transaction to `CanonicalTransaction`
   - Handle HTTP errors: 401/403 → non-recoverable auth error; 408/429/5xx → recoverable
3. **`NormalizeAsync(rawPayload)`**:
   - Parse DOMS JSON transaction object
   - Map all fields to `CanonicalTransaction` per the schema
   - Volume in microlitres, amount in minor units — convert if DOMS uses different units
   - Preserve `FccTransactionId` as opaque string
4. **`SendPreAuthAsync(command)`**:
   - HTTP POST to `http://{host}:{port}/api/v1/preauth`
   - Body: derived from `PreAuthCommand` (pumpNumber, amountMinorUnits, currencyCode, odooOrderId, customerTaxId)
   - Parse response into `PreAuthResult` (status, authorizationCode, expiresAtUtc, message)
5. **`GetPumpStatusAsync()`**:
   - HTTP GET to `http://{host}:{port}/api/v1/pump-status`
   - Parse array of pump-nozzle status objects
   - Map to `IReadOnlyList<PumpStatus>`
6. **`HeartbeatAsync()`**:
   - HTTP GET to `http://{host}:{port}/api/v1/heartbeat`
   - Return `true` if 200 OK with `{ "status": "UP" }`
   - 5-second timeout
7. Use `IHttpClientFactory` named client `"Fcc"` with appropriate timeouts
8. Create sample DOMS JSON fixtures for tests

**Acceptance criteria:**
- All 5 `IFccAdapter` methods implemented
- Normalization maps all fields correctly from DOMS format
- Pre-auth sends correct payload and parses response
- Heartbeat returns true/false correctly
- HTTP errors classified as recoverable vs non-recoverable per spec
- Unit tests with mock HTTP responses for each method (using `MockHttpMessageHandler`)
- Timeout handling works (5s for heartbeat, configurable for others)

---

### DEA-2.2: SQLite Buffer — Write, Query, Cleanup

**Sprint:** 4
**Prereqs:** DEA-0.3
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.5.3 DAO definitions, §5.5.5 Retention and cleanup
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 Edge Sync Record State Machine

**Task:**
Implement the buffer management logic on top of EF Core.

**Detailed instructions:**
1. Create `TransactionBufferManager` in `Buffer/`:
   - `BufferTransactionAsync(CanonicalTransaction tx)` — insert with local dedup (catch `DbUpdateException` on unique constraint), set `SyncStatus = Pending`
   - `GetPendingBatchAsync(int batchSize)` — get oldest Pending records for upload
   - `MarkUploadedAsync(IReadOnlyList<string> ids)` — set `SyncStatus = Uploaded`
   - `MarkDuplicateConfirmedAsync(IReadOnlyList<string> ids)` — set `SyncStatus = DuplicateConfirmed`
   - `MarkSyncedToOdooAsync(IReadOnlyList<string> fccTransactionIds)` — set `SyncStatus = SyncedToOdoo`
   - `GetForLocalApiAsync(int? pumpNumber, int limit, int offset)` — exclude SyncedToOdoo
   - `GetBufferStatsAsync()` — count by status for telemetry

2. Create `CleanupWorker` as `BackgroundService`:
   - Run periodically (from config, default 24h)
   - Delete SyncedToOdoo transactions older than `retentionDays` (default 7)
   - Delete terminal pre-auth records older than `retentionDays`
   - Trim audit log older than `retentionDays`

3. Create `IntegrityChecker`:
   - Run on app startup
   - Execute `PRAGMA integrity_check`
   - If corruption detected: backup DB file, delete, let EF Core recreate via migrations, log event for cloud telemetry

**Acceptance criteria:**
- Local dedup prevents duplicate inserts silently
- Upload batch returns records in `CreatedAt ASC` order
- Local API excludes SyncedToOdoo records
- Cleanup deletes old records correctly
- Integrity check detects and recovers from corruption
- In-memory SQLite tests for all operations

---

### DEA-2.3: Connectivity Manager & Runtime Cadence Controller

**Sprint:** 5
**Prereqs:** DEA-0.4, DEA-2.1
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.4 Connectivity State Machine
- `WIP-HLD-Edge-Agent.md` — §3.2 (operating modes), §4.2 (connectivity manager design)

**Task:**
Implement the dual-probe connectivity state machine and the single cadence controller that coordinates recurring runtime work.

**Detailed instructions:**
1. Create `ConnectivityManager` in `Connectivity/`:
2. Create a cadence controller as a single `BackgroundService`:
   - One orchestrator loop owns periodic background work
   - FCC heartbeat, cloud health check, and `SYNCED_TO_ODOO` polling are coalesced under this controller
   - Add jitter to recurring schedules to avoid synchronized bursts across devices
   - Allow cadence to adapt by ingestion mode, connectivity state, and backlog depth
3. Implement two independent probes:
   - **Internet probe**: HTTP GET to cloud `GET /health`, 5s timeout, every 30s (configurable)
   - **FCC probe**: Call adapter `HeartbeatAsync()`, 5s timeout, every 30s (configurable)
4. State derivation from probe results:
   - Both UP → `FullyOnline`
   - Internet DOWN + FCC UP → `InternetDown`
   - Internet UP + FCC DOWN → `FccUnreachable`
   - Both DOWN → `FullyOffline`
5. DOWN detection: **3 consecutive failures** required before transitioning to DOWN
6. UP recovery: **1 success** immediately transitions back to UP
7. Initialize in `FullyOffline` on app start, run both probes immediately
8. Expose state via `IObservable<ConnectivityState>` or a custom event + property pattern for UI binding
9. Log audit events on every state transition
10. Side effects on transition (per §5.4 transition table):
    - Any → InternetDown: log, increment telemetry counter, pause upload worker
    - Any → FccUnreachable: log, alert diagnostics UI, pause FCC poller
    - Any → FullyOffline: log, pause all cloud+FCC workers, local API continues
    - Any → FullyOnline: log, trigger immediate buffer replay and `SYNCED_TO_ODOO` status sync
    - InternetDown → FullyOnline: activate replay worker
    - FccUnreachable → FullyOnline: resume FCC poller from last cursor

**Acceptance criteria:**
- State correctly derived from probe results
- 3-failure threshold prevents flapping
- Single success recovers immediately
- State changes are observable (UI can bind to them)
- Transition side effects trigger (mock workers to verify)
- Unit tests for all state transitions
- Test: rapid probe alternation doesn't cause flapping
- Only one periodic cadence controller is active in the runtime
- `SYNCED_TO_ODOO` polling shares the cadence loop with cloud health checks

---

### DEA-2.4: Local REST API — Full Implementation

**Sprint:** 5–6
**Prereqs:** DEA-0.5, DEA-2.2, DEA-2.3
**Estimated effort:** 3–4 days

**Read these artifacts before starting:**
- `schemas/openapi/edge-agent-local-api.yaml` — THE definitive API spec
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.5.3 DAO queries
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.3 LAN API key validation
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 local API visibility rules

**Task:**
Implement all 7 local REST API endpoints.

**Detailed instructions:**
1. **`GET /api/transactions`** — list buffered transactions
   - Query params: `pumpNumber`, `since`, `limit`, `offset`
   - Exclude SYNCED_TO_ODOO records
   - Return paginated response with transaction list
   - Optimize for offline Odoo polling: page-bounded queries only, no live FCC dependency, and stable response time with a 30,000-record backlog
2. **`GET /api/transactions/{id}`** — get single transaction by ID
   - Return full transaction detail including raw payload
3. **`POST /api/transactions/acknowledge`** — Odoo POS marks transactions as consumed
   - Accept `{ transactionIds: [string] }`
   - This is a local-only operation — marks records for Odoo POS tracking
4. **`GET /api/pump-status`** — live pump statuses
   - Call adapter `GetPumpStatusAsync()` in real-time
   - Use short timeout and `SemaphoreSlim(1,1)` single-flight protection so concurrent callers do not fan out to FCC
   - If FCC_UNREACHABLE or live fetch exceeds timeout budget: return last-known status with `stale: true` flag and freshness metadata
5. **`POST /api/preauth`** — submit pre-authorization (delegates to PreAuthHandler — DEA-2.5)
   - Accept `PreAuthCommand` JSON
   - Return `PreAuthResult` JSON
6. **`POST /api/preauth/cancel`** — cancel pre-authorization
   - Accept `{ odooOrderId, siteCode }`
7. **`GET /api/status`** — agent status
   - Return: connectivity state, buffer stats, FCC heartbeat age, last sync timestamp, app version, uptime

8. Implement API key authentication middleware:
   - **All requests require `X-Api-Key` header** (no localhost bypass — Odoo POS is always on a different device)
   - Validate against stored API key (constant-time comparison)
   - Return 401 on missing/invalid key

**Acceptance criteria:**
- All 7 endpoints match `edge-agent-local-api.yaml` spec
- All requests require valid API key
- Transaction list excludes SYNCED_TO_ODOO records
- Pump status returns stale data when FCC unreachable
- Status endpoint returns correct connectivity state and buffer stats
- Integration tests for each endpoint using `WebApplicationFactory<T>`
- Transaction list hot-path queries meet the documented guardrail targets on representative local backlog data
- Pump status protects local API latency under concurrent access or slow FCC responses

---

### DEA-2.5: Pre-Auth Handler

**Sprint:** 6
**Prereqs:** DEA-2.1, DEA-2.2, DEA-2.3
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/specs/data-models/tier-1-1-pre-auth-record-spec.md` — pre-auth lifecycle
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.2 Pre-Auth, §5.4 connectivity states
- `docs/specs/reconciliation/tier-2-2-deduplication-strategy.md` — §5.5 Pre-auth dedup on Edge
- `schemas/canonical/pre-auth-record.schema.json` — all fields
- `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` — `sendPreAuth` method

**Task:**
Implement the pre-auth handler — relays pre-auth commands from Odoo POS to FCC via LAN.

**Detailed instructions:**
1. Create `PreAuthHandler` in `PreAuth/`:
2. On pre-auth request from Odoo POS (via local API):
   a. **Local dedup check**: Query `PreAuthRecord` by `OdooOrderId + SiteCode`
      - If exists with non-terminal status (Pending, Authorized, Dispensing): return existing record
      - If exists with terminal status (Completed, Cancelled, Expired, Failed): allow new request
   b. **Resolve FCC pump/nozzle numbers**: Query `NozzleMapping` by `SiteCode + OdooPumpNumber + OdooNozzleNumber`
      - If no match found: reject with `NOZZLE_MAPPING_NOT_FOUND` error — do NOT send to FCC
      - If inactive nozzle: reject with `NOZZLE_INACTIVE` error
      - On success: extract `FccPumpNumber`, `FccNozzleNumber`, `ProductCode` from the result
   c. **Check connectivity**: If `FccUnreachable` or `FullyOffline`, reject with `FCC_UNREACHABLE` error
   d. Create local `PreAuthRecord` with `Status = Pending`
   e. Call adapter `SendPreAuthAsync(command)` with **FCC numbers** — sends to FCC over LAN
   f. Update local record based on FCC response:
      - Authorized → set status, authorizationCode, expiresAtUtc
      - Declined/Timeout/Error → set status Failed with failure reason
   g. Mark `IsCloudSynced = false` — queued for cloud forwarding
   h. Return result to Odoo POS immediately
3. Pre-auth is ALWAYS via LAN, regardless of internet state
4. Handle FCC timeouts (configurable, default 30s)
5. Cloud forwarding must be fully asynchronous and must never block the local request-response path
6. Implement pre-auth cancellation:
   - Find record by `OdooOrderId`
   - If Pending or Authorized: attempt FCC deauthorization, set status Cancelled
   - If Dispensing: cannot cancel (pump is active)
7. Implement pre-auth expiry checker (periodic, via cadence controller):
   - Query pre-auths past `ExpiresAt` that are still Pending/Authorized/Dispensing
   - Transition to Expired, attempt FCC deauthorization (best-effort)

**Acceptance criteria:**
- Pre-auth sent to FCC via LAN and result returned to Odoo POS
- Local dedup prevents duplicate pre-auths for same order
- Terminal-status records allow re-request
- FCC_UNREACHABLE properly rejects pre-auth
- Cancellation works for Pending/Authorized
- Expiry checker cleans up old pre-auths
- Record marked for cloud sync
- Unit tests for all dedup/connectivity/status scenarios
- Cloud unavailability does not materially degrade pre-auth response time on a healthy FCC LAN

---

### DEA-2.6: Ingestion Orchestrator

**Sprint:** 6–7
**Prereqs:** DEA-2.1, DEA-2.2, DEA-2.3
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `WIP-HLD-Edge-Agent.md` — §4.3 (ingestion orchestrator), §3.2 (operating modes), §6 (ingestion modes)
- `schemas/config/edge-agent-config.schema.json` — `ingestionMode` field
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 Edge Sync, §5.4 Connectivity

**Task:**
Implement the ingestion orchestrator that routes transactions based on ingestion mode and connectivity state.

**Detailed instructions:**
1. Create `IngestionOrchestrator` in `Ingestion/`:
2. Create `FccPoller` — periodic task that:
   - Uses adapter `FetchTransactionsAsync(cursor)` to poll FCC over LAN
   - Runs on a configurable interval (`pullIntervalSeconds` from config)
   - Advances cursor using `SyncState.LastFccCursor`
   - For each transaction returned: pass to buffer manager
   - Uses cadence supplied by the runtime cadence controller rather than a second independent timer loop
3. Implement ingestion mode routing:
   - **CLOUD_DIRECT**: Agent runs safety-net LAN poller on a longer interval (e.g., 5 minutes), buffers locally, uploads to cloud (cloud dedup handles dual-path)
   - **RELAY**: Agent is primary receiver. Polls FCC on normal interval (e.g., 30s), buffers locally, uploads to cloud
   - **BUFFER_ALWAYS**: Same as RELAY but explicit local-first semantics
4. Respect connectivity state:
   - FullyOnline: poll FCC + buffer + upload
   - InternetDown: poll FCC + buffer (no upload)
   - FccUnreachable: no polling (existing buffer accessible, upload continues if internet up)
   - FullyOffline: nothing (local API serves stale buffer)
5. On connectivity recovery (InternetDown → FullyOnline):
   - Trigger immediate upload of all Pending records

**Acceptance criteria:**
- FCC poller fetches transactions on schedule
- Cursor advances correctly between polls
- Ingestion mode affects polling interval and behavior
- Connectivity state properly stops/resumes polling
- Recovery triggers immediate upload
- Unit tests for each mode + connectivity combination
- Poll scheduling is driven by the shared cadence controller rather than multiple competing timers

---

### DEA-2.7: Manual FCC Pull API

**Sprint:** 6
**Prereqs:** DEA-2.1, DEA-2.2, DEA-2.4
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `Requirements.md` — REQ-15.7 Attendant-Triggered Manual Pull
- `HighLevelRequirements.md` — §15.7
- `schemas/openapi/edge-agent-local-api.yaml`

**Task:**
Implement an on-demand FCC pull endpoint so Odoo POS can surface a just-completed dispense without waiting for the next scheduled poll.

**Detailed instructions:**
1. Add a local API endpoint for manual FCC pull
2. Trigger an immediate adapter fetch using the current cursor and buffer any newly discovered transactions
3. Return a structured response summarizing newly buffered transactions and cursor movement
4. Ensure the operation is serialized with the scheduled poller so manual pull and background polling do not race (use `SemaphoreSlim`)
5. Apply the same deduplication and normalization rules as scheduled ingestion

**Acceptance criteria:**
- Odoo POS can trigger manual FCC pull through the local API
- Newly discovered transactions are buffered and immediately available to offline transaction reads
- Manual pull does not corrupt cursor state or race with scheduled polling
- Unit tests cover no-op pull, new-transaction pull, and concurrent manual/scheduled pull scenarios

---

## Phase 3 — Cloud ↔ Desktop Agent Integration (Sprints 6–8)

### DEA-3.1: Cloud Upload Worker

**Sprint:** 7
**Prereqs:** DEA-2.2, DEA-2.3
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `WIP-HLD-Edge-Agent.md` — §4.4 (cloud sync engine), §5.3 (upload flow)
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/transactions/upload`
- `docs/specs/reconciliation/tier-2-2-deduplication-strategy.md` — §5.4 Edge pre-filtering
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 PENDING → UPLOADED
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.1 device JWT auth

**Task:**
Implement the cloud upload worker that sends buffered transactions to the cloud.

**Detailed instructions:**
1. Create `CloudUploadWorker` as `BackgroundService` in `Sync/`:
2. Runs periodically when internet is available (`syncIntervalSeconds` from config)
3. Upload algorithm:
   a. Query Pending records ordered by `CreatedAt ASC` (oldest first)
   b. Batch into groups of `uploadBatchSize` (from config, default 50)
   c. Send batch to `POST /api/v1/transactions/upload` with device JWT
   d. Process per-record response:
      - `status: "created"` → mark `SyncStatus = Uploaded`
      - `status: "skipped", reason: "DUPLICATE"` → mark `SyncStatus = DuplicateConfirmed`
   e. On HTTP failure: increment `UploadAttempts`, set `LastUploadError`, retry on next cycle
4. **NEVER skip past a failed record** — retry the oldest Pending batch first
5. Handle JWT expiry: on 401, trigger token refresh, retry
6. Handle 403 DEVICE_DECOMMISSIONED: stop all sync, show alert
7. Implement exponential backoff with Polly: 1s, 2s, 4s, 8s, max 60s
8. Suspend when connectivity state is InternetDown or FullyOffline
9. Resume immediately on FullyOnline recovery (triggered by ConnectivityManager)
10. Use `IHttpClientFactory` named client `"Cloud"` for all cloud requests

**Acceptance criteria:**
- Uploads Pending records in chronological order
- Per-record response handled correctly (Uploaded vs DuplicateConfirmed)
- Failed records retried with backoff
- Never skips past failed record
- JWT refresh on 401
- Decommission handling on 403
- Suspends when offline, resumes on recovery
- Unit tests for upload logic, response handling, retry logic

---

### DEA-3.2: SYNCED_TO_ODOO Status Poller

**Sprint:** 7
**Prereqs:** DEA-3.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `GET /api/v1/transactions/synced-status`
- `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` — §5.3 UPLOADED → SYNCED_TO_ODOO

**Task:**
Implement the worker that polls cloud for SYNCED_TO_ODOO status updates.

**Detailed instructions:**
1. Create `StatusPollWorker` in `Sync/`:
2. Periodically poll `GET /api/v1/transactions/synced-status?since={lastPollTimestamp}`
3. For each `fccTransactionId` returned: call `BufferManager.MarkSyncedToOdooAsync()`
4. Update `SyncState.LastStatusPollAt`
5. Runs on `statusPollIntervalSeconds` from config (default 60s)
6. Only runs when internet is available
7. Records at SyncedToOdoo are excluded from local API responses

**Acceptance criteria:**
- Correctly transitions Uploaded → SyncedToOdoo locally
- Last poll timestamp advances correctly
- Marked records excluded from local API
- Suspends when offline

---

### DEA-3.3: Config Poll Worker

**Sprint:** 7
**Prereqs:** DEA-0.6
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `GET /api/v1/agent/config`
- `schemas/config/edge-agent-config.schema.json` — config model
- `docs/specs/config/tier-2-4-edge-agent-configuration-schema.md` — hot-reload vs restart fields

**Task:**
Implement the config pull worker.

**Detailed instructions:**
1. Create `ConfigPollWorker` in `Sync/`:
2. Periodically poll `GET /api/v1/agent/config` with `If-None-Match: {currentConfigVersion}`
3. On 304: no-op
4. On 200: parse new config, store in `AgentConfig` table, apply changes
5. Create `ConfigManager` that:
   - Holds current config in memory (`IOptionsMonitor<T>` pattern)
   - On config update: hot-reload changed values (poll intervals, batch sizes, log level)
   - Identify fields requiring restart (FCC host/port, Kestrel port) — log warning, notify UI, don't force restart
6. Use `SyncState.LastConfigVersion` to track version

**Acceptance criteria:**
- Config pulled and stored locally
- Hot-reloadable fields take effect immediately
- 304 response handled efficiently (no re-parse)
- Config version tracked in sync state

---

### DEA-3.4: Telemetry Reporter

**Sprint:** 7
**Prereqs:** DEA-2.3, DEA-2.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/agent/telemetry`
- `schemas/canonical/telemetry-payload.schema.json` — all telemetry fields
- `docs/specs/data-models/tier-1-1-telemetry-payload-spec.md` — field definitions

**Task:**
Implement telemetry reporting to cloud.

**Detailed instructions:**
1. Create `TelemetryReporter` in `Sync/`:
2. Collect telemetry data:
   - CPU usage (Process.GetCurrentProcess())
   - Memory usage (GC stats, working set)
   - Disk free space (DriveInfo)
   - Buffer depth (count by sync status from DbContext)
   - FCC heartbeat age (time since last successful heartbeat)
   - Last sync timestamp
   - Sync lag (oldest Pending record age)
   - App version (Assembly version)
   - Error counts (accumulated since last report)
   - Connectivity state
   - OS platform and version
3. Send `POST /api/v1/agent/telemetry` on `telemetryIntervalSeconds` (default 300s)
4. Fire-and-forget: if send fails, skip (no buffering of telemetry)
5. Only send when internet is available

**Acceptance criteria:**
- All telemetry fields populated from real system data
- CPU, memory, storage, buffer stats accurate
- Sends on schedule, skips when offline
- No telemetry buffering (fire-and-forget)

---

### DEA-3.5: Device Registration & Provisioning Flow

**Sprint:** 8
**Prereqs:** DEA-0.6
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.1 Device Registration Flow
- `docs/specs/data-models/tier-1-1-device-registration-spec.md` — registration models
- `schemas/canonical/device-registration.schema.json` — request/response

**Task:**
Implement the provisioning/registration flow with a setup wizard UI.

**Detailed instructions:**
1. Create a provisioning flow in the Avalonia UI:
   - **Option A: One-time registration code** (primary):
     a. User enters a registration code (generated in cloud admin portal)
     b. App calls `POST /api/v1/agent/register` with `{ registrationCode, deviceFingerprint, appVersion }`
     c. On success: receive `{ deviceId, deviceToken, refreshToken, siteCode, config }`
   - **Option B: Manual configuration** (fallback):
     a. Setup wizard with fields: Cloud URL, Site Code, FCC Host, FCC Port
     b. App calls same registration endpoint with manual details

2. On successful registration:
   a. Store `deviceToken` + `refreshToken` in platform-specific secure storage:
      - Windows: DPAPI via `ProtectedData` class
      - macOS: Keychain via Security framework interop
      - Linux: libsecret via D-Bus or fallback to encrypted file with machine-key
   b. Store `deviceId`, `siteCode`, `legalEntityId`, `cloudBaseUrl`, `fccHost`, `fccPort` in encrypted config file
   c. Store initial config in `AgentConfig` table
   d. Start all background services

3. Implement token refresh via `DelegatingHandler` in HTTP pipeline:
   - On 401: call `POST /api/v1/agent/token/refresh` with stored refresh token
   - On new tokens: update secure storage
   - On refresh failure: enter `REGISTRATION_REQUIRED` state, show alert in UI

4. Handle 403 DEVICE_DECOMMISSIONED: stop all sync, show "Device Decommissioned" screen

5. On app startup: check for existing registration. If registered, start services. If not, show provisioning wizard.

6. Create `ICredentialStore` abstraction with platform-specific implementations:
   - `Task StoreAsync(string key, string value)`
   - `Task<string?> RetrieveAsync(string key)`
   - `Task RemoveAsync(string key)`

**Acceptance criteria:**
- Registration code flow works end-to-end
- Manual configuration fallback works
- Tokens stored securely per platform
- Token refresh works transparently on 401
- Decommission handling shows appropriate UI
- First launch shows provisioning wizard; subsequent launches start services directly
- Credential store works on all three platforms

---

### DEA-3.6: Pre-Auth Cloud Forwarding

**Sprint:** 8
**Prereqs:** DEA-2.5, DEA-3.1
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `schemas/openapi/cloud-api.yaml` — `POST /api/v1/preauth`
- `docs/specs/data-models/tier-1-4-database-schema-design.md` — §5.5.1 `is_cloud_synced` field

**Task:**
Implement forwarding of local pre-auth records to cloud.

**Detailed instructions:**
1. Create `PreAuthCloudForwardWorker` in `Sync/`:
2. Query `PreAuthRecord` where `IsCloudSynced = false`, ordered by `CreatedAt ASC`, limit N
3. Send each to `POST /api/v1/preauth` on cloud
4. On success: mark `IsCloudSynced = true`
5. On failure: increment `CloudSyncAttempts`, retry on next cycle
6. Runs periodically when internet is available

**Acceptance criteria:**
- Unsynced pre-auth records forwarded to cloud
- Synced flag updated on success
- Retry on failure with attempt counter
- Suspends when offline

---

## Phase 4 — GUI & User Experience (Sprints 8–9)

### DEA-4.1: Avalonia Shell, System Tray & Navigation

**Sprint:** 8
**Prereqs:** DEA-0.2, DEA-2.3
**Estimated effort:** 2 days

**Task:**
Implement the main application shell with navigation, system tray integration, and responsive layout.

**Detailed instructions:**
1. Create `MainWindow` with sidebar navigation:
   - Dashboard (default view)
   - Transactions
   - Pre-Auth
   - Configuration
   - Logs
2. System tray integration:
   - Tray icon with connectivity-based color (green/yellow/red)
   - Context menu: Show Window, Dashboard, Restart Agent, Check for Updates, Exit
   - Double-click tray icon opens window
   - Close button minimizes to tray (with first-time notification)
3. Apply consistent theme (light/dark mode support)
4. Responsive layout that works on various screen sizes
5. Status bar showing: connectivity state, buffer depth, last sync time

**Acceptance criteria:**
- Navigation between all views works
- System tray icon reflects connectivity state
- Minimize to tray on close
- Status bar updates in real-time
- App remembers window position and size

---

### DEA-4.2: Diagnostics Dashboard

**Sprint:** 9
**Prereqs:** DEA-4.1, all Phase 2 + Phase 3 tasks
**Estimated effort:** 2 days

**Task:**
Implement the diagnostics dashboard for site supervisors.

**Detailed instructions:**
1. Create Dashboard view displaying:
   - Connectivity state (color-coded indicator: green/yellow/red)
   - FCC connection status + last heartbeat time
   - Buffer stats: Pending count, Uploaded count, SyncedToOdoo count (as cards or gauges)
   - Last cloud sync timestamp + sync lag
   - CPU, memory, disk usage
   - App version, device ID, site code
   - Uptime
2. Create Transactions view:
   - Paginated table of buffered transactions
   - Filter by: sync status, pump number, date range
   - Click to view full transaction detail + raw payload
3. Create Logs view:
   - Recent audit log entries (last 100)
   - Filter by severity, search by text
4. Manual action buttons:
   - Force cloud sync
   - Force FCC poll
   - Clear synced cache
5. Real-time updates using `IObservable` bindings or timer-based refresh

**Acceptance criteria:**
- All status fields displaying real-time data
- Color-coded connectivity indicator
- Transaction table loads fast with large datasets
- Manual sync/poll triggers work
- Logs are searchable and filterable

---

### DEA-4.3: Setup Wizard UI

**Sprint:** 9
**Prereqs:** DEA-3.5
**Estimated effort:** 1 day

**Task:**
Polish the first-launch setup wizard experience.

**Detailed instructions:**
1. Multi-step wizard flow:
   - Step 1: Welcome + choose registration method (code vs manual)
   - Step 2a: Enter registration code → validate with cloud
   - Step 2b: Enter manual config (cloud URL, site code, FCC host/port)
   - Step 3: Connection test (ping cloud + ping FCC)
   - Step 4: Success summary → launch agent
2. Input validation with clear error messages
3. Connection test with progress indicators
4. Ability to go back and modify entries
5. Show API key for HHT configuration (copyable)

**Acceptance criteria:**
- Wizard guides user through complete setup
- Validation errors are clear and actionable
- Connection test verifies both cloud and FCC connectivity
- API key is displayed and copyable for HHT setup

---

### DEA-4.4: Configuration Screen

**Sprint:** 9
**Prereqs:** DEA-3.3
**Estimated effort:** 1 day

**Task:**
Implement the settings/configuration view.

**Detailed instructions:**
1. Display current configuration (read from `AgentConfig` + local overrides):
   - FCC connection: host, port, vendor (read-only if cloud-managed)
   - Cloud connection: URL, device ID (read-only)
   - Polling intervals: FCC poll, cloud sync, telemetry
   - Buffer settings: retention days, upload batch size
   - API settings: port, API key (regenerate option)
   - Auto-update: enable/disable, check now button
   - Log level: adjustable at runtime
2. Indicate which settings are cloud-managed vs locally configurable
3. Restart-required settings show a warning
4. Save + Apply button for local changes

**Acceptance criteria:**
- All configurable settings displayed
- Cloud-managed settings are clearly marked
- Local changes take effect on save (hot-reload where supported)
- Restart warning for settings that require it

---

## Phase 6 — Hardening & Production Readiness (Sprints 10–12)

### DEA-6.1: Offline Scenario Stress Testing

**Sprint:** 10
**Prereqs:** All Phase 2 + Phase 3 tasks
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/specs/testing/testing-strategy.md` — offline scenario testing section
- `WIP-HLD-Edge-Agent.md` — §8.6 (resilience), §9 (risk analysis)

**Task:**
Design and execute offline scenario tests.

**Detailed instructions:**
1. Test scenarios:
   - Internet drop during upload batch (partial success) → verify retry resumes correctly
   - FCC LAN drop during poll → verify graceful degradation
   - 1-hour internet outage → verify buffer captures all transactions, replay succeeds
   - 24-hour internet outage → verify 30,000+ records buffered without issues
   - 7-day simulated outage → verify buffer integrity and replay ordering
   - Power loss during SQLite write → verify WAL recovery
   - App crash / kill → verify state recovery on restart
   - OS sleep/hibernate → verify reconnection on wake
2. Use mock FCC and mock cloud endpoints for deterministic testing
3. Verify: zero transaction loss in all scenarios
4. Measure: memory usage, SQLite performance at 30K records, CPU usage
5. Validate results against the performance guardrails defined at the top of this document

**Acceptance criteria:**
- Zero transactions lost in any scenario
- Buffer handles 30,000+ records without degradation
- WAL mode recovers from simulated power loss
- Upload replay maintains chronological order after recovery
- Measured latency, memory, and replay results are compared against the documented guardrail thresholds
- OS sleep/wake recovery tested on all three platforms

---

### DEA-6.2: Security Hardening

**Sprint:** 11
**Prereqs:** DEA-3.5
**Estimated effort:** 1–2 days

**Read these artifacts before starting:**
- `docs/specs/security/tier-2-5-security-implementation-plan.md` — §5.3 Edge Agent Security

**Task:**
Verify and harden Desktop Edge Agent security.

**Detailed instructions:**
1. Verify credential storage: tokens stored in platform-specific secure storage, not plaintext
2. Verify config encryption: sensitive fields (FCC credentials, API keys) are encrypted at rest
3. Verify log redaction: no sensitive fields in log output (use Serilog destructuring policies)
4. Verify API key validation: all requests require valid key (constant-time comparison)
5. Verify TLS configuration for cloud communication
6. Verify SQLite database file permissions (not world-readable)
7. Add `[SensitiveData]` attribute and Serilog enricher to prevent sensitive field serialization
8. Verify the app doesn't expose unnecessary network ports

**Acceptance criteria:**
- No plaintext tokens/credentials on disk
- Log output contains no sensitive data
- API key enforcement works
- Database file has restrictive permissions
- TLS enforced for all cloud communication

---

### DEA-6.3: Cross-Platform Testing

**Sprint:** 11
**Prereqs:** All prior tasks
**Estimated effort:** 2 days

**Task:**
Comprehensive testing on all three target platforms.

**Detailed instructions:**
1. Test matrix:
   | Feature | Windows 10/11 | macOS 12+ (Intel + ARM) | Ubuntu 22.04 |
   |---------|--------------|------------------------|---------------|
   | Installer | Setup.exe | .app bundle | AppImage |
   | Auto-update | Delta update | Full update | Full update |
   | Credential store | DPAPI | Keychain | libsecret |
   | Service mode | Windows Service | launchd | systemd |
   | GUI | Avalonia | Avalonia | Avalonia |
   | System tray | Win32 tray | macOS menu bar | freedesktop tray |

2. Verify on each platform:
   - Clean install and first-launch wizard
   - FCC LAN communication
   - Cloud sync
   - Auto-update
   - Service mode install/uninstall
   - Credential storage and retrieval
   - System tray behavior
   - Sleep/wake recovery

**Acceptance criteria:**
- All features work on all three platforms
- Platform-specific behaviors (tray, credentials, service) are correct
- No platform-specific crashes or UI rendering issues
- Installer works on minimum OS versions

---

## Key Differences from Android Edge Agent

| Aspect | Android Edge Agent | Desktop Edge Agent |
|--------|-------------------|-------------------|
| Language | Kotlin | C# (.NET 10) |
| GUI | Android Activities/Fragments | Avalonia UI (MVVM) |
| Database | Room ORM + SQLite | EF Core + SQLite |
| HTTP Server | Ktor (CIO) | ASP.NET Core (Kestrel) |
| HTTP Client | Ktor Client (OkHttp) | HttpClient (IHttpClientFactory) |
| DI | Koin | Microsoft.Extensions.DependencyInjection |
| Background Work | Foreground Service + Coroutines | IHostedService + async/await |
| Credential Storage | Android Keystore | DPAPI / Keychain / libsecret |
| API Auth | Localhost bypass + LAN API key | All requests require API key (no localhost bypass) |
| Provisioning | QR code scan | Registration code or manual config |
| Updates | Sure MDM | Velopack auto-update |
| Service Mode | Android Foreground Service (START_STICKY) | Windows Service / systemd / launchd |
| Target Hardware | Urovo i9100 (constrained) | PC/Mac/Linux (more resources) |
| Network Binding | localhost by default, optional LAN | LAN by default (0.0.0.0) |

## Changelog

### 2026-03-11

- Initial plan created based on requirements analysis and Edge Agent Android plan
- Technology stack selected: .NET 10, Avalonia UI, Kestrel, EF Core + SQLite, Velopack
- Performance guardrails adapted for desktop hardware (tighter latency, more generous memory)
- All API requests require API key (no localhost bypass since Odoo POS is always remote)
- Provisioning via registration code or manual config (no QR scanning)
- Auto-update via Velopack (cross-platform)
- GUI shell with system tray, diagnostics dashboard, setup wizard, and configuration screen
- Headless service mode supported for server/kiosk deployments
