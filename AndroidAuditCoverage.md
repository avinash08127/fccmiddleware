# Android Audit Coverage — FCC Edge Agent

**Project**: `fcc-edge-agent`
**Last updated**: 2026-03-13

---

## Screen-Level Coverage

| Screen | Module | Status |
|--------|--------|--------|
| SplashActivity | UI / Entry | Reviewed |
| LauncherActivity | UI / Navigation | Reviewed |
| ProvisioningActivity | UI / Provisioning | Reviewed |
| DiagnosticsActivity | UI / Diagnostics | Reviewed |
| SettingsActivity | UI / Settings | Reviewed |
| DecommissionedActivity | UI / Lifecycle | Reviewed |
| EdgeAgentForegroundService | Service / Core | Reviewed |

---

## Module-Cluster Coverage

| Module | Package | Findings | Status |
|--------|---------|----------|--------|
| API (Local REST) | `api` | API, Functional, Security | Reviewed |
| Adapter (FCC Serial) | `adapter` | Reliability, Technical | Reviewed |
| Buffer (Room DB) | `buffer` | Functional, Performance, Technical | Reviewed |
| Config | `config` | Security | Reviewed |
| Connectivity | `connectivity` | Reliability | Reviewed |
| DI (Koin) | `di` | Technical, Reliability | Reviewed |
| Ingestion | `ingestion` | Functional, Performance, Technical | Reviewed |
| Logging | `logging` | Performance, Security | Reviewed |
| Pre-Auth | `preauth` | Functional, Security | Reviewed |
| Runtime (Cadence) | `runtime` | Technical, Performance | Reviewed |
| Security | `security` | Security | Reviewed |
| Sync (Cloud Workers) | `sync` | Functional, API, Performance | Reviewed |
| UI (Activities) | `ui` | All six findings files | Reviewed |
| WebSocket | `websocket` | API, Reliability | Reviewed |

---

## Findings File Index

| Findings File | Focus Area |
|---------------|------------|
| AndroidApiFindings.md | Networking, HTTP client, DTOs, serialization, auth tokens |
| AndroidReliabilityFindings.md | Lifecycle safety, coroutine scopes, config changes, StateFlow |
| AndroidFunctionalFindings.md | End-to-end correctness, state management, credential handling |
| AndroidPerformanceFindings.md | DB queries, memory/GC, UI rendering, logging I/O |
| AndroidSecurityFindings.md | Credential storage, PII, FLAG_SECURE, LAN API, log sharing |
| AndroidTechnicalFindings.md | Architecture, DI patterns, separation of concerns, threading |

---

## Cross-Reference: Screen × Finding Dimension

| Screen | API | Reliability | Functional | Performance | Security | Technical |
|--------|-----|-------------|------------|-------------|----------|-----------|
| SplashActivity | — | ✓ | — | ✓ | ✓ | — |
| LauncherActivity | — | ✓ | — | — | ✓ | — |
| ProvisioningActivity | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| DiagnosticsActivity | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| SettingsActivity | ✓ | ✓ | ✓ | — | ✓ | — |
| DecommissionedActivity | — | ✓ | ✓ | — | ✓ | — |
| EdgeAgentForegroundService | — | ✓ | ✓ | ✓ | ✓ | ✓ |
