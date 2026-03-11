# Desktop Edge Agent — Agent System Prompt

**Use this prompt as context when assigning ANY Desktop Edge Agent task to an AI coding agent.**

---

## You Are Working On

The **Forecourt Middleware Desktop Edge Agent** — a cross-platform .NET desktop application that runs on PCs, Macs, or Linux computers at fuel retail sites. It bridges the cloud middleware and the station-local Forecourt Controller (FCC) over WiFi LAN, providing the same core functionality as the Android Edge Agent but for sites with a dedicated computer.

## What This System Does

1. **Polls the FCC** over station WiFi LAN to fetch fuel dispensing transactions
2. **Buffers transactions locally** in SQLite (EF Core) when internet is unavailable
3. **Uploads buffered transactions** to the cloud backend in chronological batches when connectivity returns
4. **Relays pre-authorization commands** from Odoo POS (on HHTs over LAN) to the FCC over LAN (always via LAN, never cloud)
5. **Exposes a local REST API** (ASP.NET Core Kestrel on port 8585) for Odoo POS on HHTs to query transactions and submit pre-auths over the station LAN
6. **Monitors connectivity** — dual-probe system checking both internet (cloud) and FCC (LAN) independently
7. **Reports telemetry** to cloud — CPU, memory, storage, buffer depth, FCC heartbeat, sync status
8. **Self-registers** with cloud via a one-time registration code or manual configuration
9. **Receives configuration** from cloud and applies it at runtime (hot-reload where possible)
10. **Supports manual FCC pull** so Odoo POS or supervisor workflows can surface a just-completed dispense without waiting for the next scheduled poll
11. **Provides a GUI** — Avalonia-based diagnostics dashboard, setup wizard, and system tray integration

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 10 |
| Language | C# |
| Target Platforms | Windows 10+, macOS 12+, Ubuntu 22.04+ / Fedora 38+ |
| GUI Framework | Avalonia UI 11 (Skia-based, cross-platform MVVM) |
| Local REST API | ASP.NET Core (Kestrel) on port 8585 |
| HTTP Client | IHttpClientFactory + HttpClient |
| Database | EF Core 10 + SQLite (WAL mode) |
| DI | Microsoft.Extensions.DependencyInjection |
| Background Workers | IHostedService / BackgroundService |
| Credential Storage | DPAPI (Windows), Keychain (macOS), libsecret (Linux) via ICredentialStore abstraction |
| Installer/Updater | Velopack (cross-platform auto-update) |
| Logging | Serilog + Microsoft.Extensions.Logging |
| Serialization | System.Text.Json |
| Testing | xUnit, NSubstitute, FluentAssertions, Microsoft.EntityFrameworkCore.InMemory |

## Project Structure

```
src/desktop-edge-agent/
├── FccDesktopAgent.sln
├── src/
│   ├── FccDesktopAgent.Core/              # Class library — domain, business logic
│   │   ├── Adapter/
│   │   │   ├── Common/                    # IFccAdapter interface, shared types, enums
│   │   │   └── Doms/                      # DOMS adapter implementation
│   │   ├── Buffer/                        # EF Core DbContext, entities, buffer manager
│   │   ├── Connectivity/                  # Connectivity state machine
│   │   ├── Ingestion/                     # Ingestion orchestrator, FCC poller
│   │   ├── PreAuth/                       # Pre-auth handler
│   │   ├── Sync/                          # Cloud upload, status poll, config poll, telemetry
│   │   ├── Config/                        # Configuration manager
│   │   ├── Security/                      # ICredentialStore, sensitive field handling
│   │   └── Runtime/                       # Cadence controller, AgentHostBuilder
│   ├── FccDesktopAgent.Api/               # Class library — Kestrel REST API endpoints
│   ├── FccDesktopAgent.App/               # Avalonia UI executable — GUI shell
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Assets/
│   │   └── Program.cs
│   └── FccDesktopAgent.Service/           # Console executable — headless service mode
└── tests/
    ├── FccDesktopAgent.Core.Tests/
    ├── FccDesktopAgent.Api.Tests/
    └── FccDesktopAgent.Integration.Tests/
```

## Key Architecture Rules

1. **Offline-first**: The agent MUST function when internet is down. LAN operations (FCC poll, pre-auth, local API) never depend on cloud.
2. **No transaction left behind**: Every transaction polled from FCC is buffered locally. Upload failures are retried. Replay is in chronological order (oldest first).
3. **SQLite WAL mode**: Always enabled. Required for crash resilience.
4. **Generic Host is the backbone**: All background work runs as `IHostedService` implementations. Both the GUI app and headless service share the same host builder.
5. **Structured concurrency**: Use `CancellationToken` everywhere. Background workers respect `StoppingToken`. Never use fire-and-forget `Task.Run` without cancellation support.
6. **Currency**: `long` minor units (cents). NEVER floating point for money.
7. **Dates**: `DateTimeOffset` in code, stored as ISO 8601 UTC text in SQLite.
8. **IDs**: UUID v4 strings for middleware-generated IDs. Preserve FCC IDs as opaque strings.
9. **Logging**: NEVER log sensitive fields (FCC credentials, tokens, customer TIN). Use `[SensitiveData]` attribute and Serilog destructuring policies.
10. **One cadence controller**: Recurring runtime work is coalesced under a single `BackgroundService`. Do not introduce independent timer loops for heartbeat, status sync, config polling, telemetry, and replay.
11. **Pre-auth is the top latency path**: `POST /api/preauth` must respond based on LAN-only work. Cloud forwarding is always asynchronous and never on the request path.
12. **Offline reads are buffer-backed**: `GET /api/transactions` must never depend on live FCC access and must remain performant with a 30,000-record backlog.
13. **Pump status is live but bounded**: `GET /api/pump-status` should use short timeouts, `SemaphoreSlim` single-flight protection, and last-known stale fallback metadata when FCC is slow or unreachable.
14. **All API requests require API key**: Unlike the Android agent, there is no localhost bypass. Odoo POS is always on a separate device (HHT), so every request travels over LAN and must include `X-Api-Key`.
15. **Cross-platform abstractions**: Use `ICredentialStore` for platform-specific credential storage. Use `Environment.SpecialFolder` or platform checks for file paths. Never hardcode Windows paths.
16. **Dual-mode app**: The same core logic runs in both GUI mode (Avalonia + tray) and headless service mode (Windows Service / systemd / launchd).

## Performance Guardrails

- `POST /api/preauth` p95 local API overhead: <= 50 ms before FCC call time
- `POST /api/preauth` p95 end-to-end on healthy FCC LAN: <= 1.5 s; p99 <= 3 s
- `GET /api/transactions` p95 for first page (`limit <= 50`) with 30,000 buffered records: <= 100 ms
- `GET /api/status` p95: <= 50 ms
- `GET /api/pump-status` live-response target on healthy LAN: <= 1 s; stale fallback: <= 50 ms
- Steady-state RSS target: <= 250 MB during normal operation
- Replay throughput target on stable internet: >= 600 transactions/minute while preserving chronological ordering
- Cold start to API-ready: <= 5 seconds
- Installer size target: <= 60 MB (self-contained single-file)

## Must-Read Artifacts (Before ANY Task)

| Artifact | Path | What It Tells You |
|----------|------|-------------------|
| Edge Agent HLD | `WIP-HLD-Edge-Agent.md` | Architecture, all flows, operating modes |
| Canonical Transaction Schema | `schemas/canonical/canonical-transaction.schema.json` | Transaction field contract |
| Pre-Auth Record Schema | `schemas/canonical/pre-auth-record.schema.json` | Pre-auth lifecycle model |
| Pump Status Schema | `schemas/canonical/pump-status.schema.json` | Pump state model |
| Telemetry Schema | `schemas/canonical/telemetry-payload.schema.json` | Health metrics structure |
| Edge Local API Spec | `schemas/openapi/edge-agent-local-api.yaml` | All local REST endpoints |
| Edge Room Schema | `db/ddl/002-edge-room-schema.sql` | SQLite table definitions (reference) |
| Edge Agent Config Schema | `schemas/config/edge-agent-config.schema.json` | Runtime configuration model |
| State Machines | `docs/specs/state-machines/tier-1-2-state-machine-formal-definitions.md` | Edge sync, connectivity, pre-auth state machines |
| FCC Adapter Contracts | `docs/specs/foundation/tier-1-5-fcc-adapter-interface-contracts.md` | IFccAdapter interface (adapt for C#) |
| Database Schema Design | `docs/specs/data-models/tier-1-4-database-schema-design.md` | Edge entities, indexes (adapt for EF Core) |
| Security Plan | `docs/specs/security/tier-2-5-security-implementation-plan.md` | Credential storage, API key, TLS |
| Device Registration Spec | `docs/specs/data-models/tier-1-1-device-registration-spec.md` | Registration flow |
| Desktop Agent Development Plan | `docs/plans/dev-plan-desktop-edge-agent.md` | Task sequencing, performance guardrails |

## Connectivity States

| State | Internet | FCC LAN | Behaviour |
|-------|----------|---------|-----------|
| FullyOnline | UP | UP | Normal: poll FCC, upload to cloud, sync status, report telemetry |
| InternetDown | DOWN | UP | Poll FCC, buffer locally, pre-auth via LAN, local API serves full buffer |
| FccUnreachable | UP | DOWN | Can't poll FCC, but upload existing buffer, sync status from cloud |
| FullyOffline | DOWN | DOWN | Local API serves stale buffer only, alert supervisor |

State transitions use **3 consecutive failures** for DOWN, **1 success** for UP recovery.

## Edge Sync Record States

```
Pending → Uploaded → SyncedToOdoo → Archived → (deleted)
```

Upload is in `CreatedAt ASC` order. Never skip past a failed record.

## Ingestion Modes (from site config)

| Mode | Behaviour |
|------|-----------|
| CLOUD_DIRECT | FCC pushes directly to cloud. Agent is safety-net LAN poller (catch-up only). |
| RELAY | Agent is primary receiver. Polls FCC, buffers, uploads to cloud. |
| BUFFER_ALWAYS | Agent always buffers locally first, then uploads. |

## Testing Standards

- Domain logic: xUnit with NSubstitute
- EF Core queries: In-memory SQLite database tests
- API endpoints: `WebApplicationFactory<T>` integration tests
- Connectivity manager: Unit tests with mocked probes
- Cross-platform: CI matrix on Windows, macOS, Linux
- Performance benchmarks: measure local API latency, replay throughput, and backlog query performance against a representative 30,000-record dataset

## Implementation Priorities

- Keep the runtime minimal; defer non-critical work unless it is piggybacked on an existing successful cycle
- Prefer one coalesced cadence loop over multiple recurring timers
- Optimize first for Odoo-visible latency: pre-auth, offline transaction reads, status endpoint, then pump-status fallback behavior
- Treat manual FCC pull as a core requirement, not an optional convenience feature
- All platform-specific code must be behind an abstraction (ICredentialStore, data directory resolution, etc.)
- Test on all three platforms early and often — not just in hardening
