# Virtual Lab — Agent System Prompt

**Use this prompt as context when assigning ANY Virtual Lab task to an AI coding agent.**

---

## You Are Working On

The **FCC Simulator + Pump Simulator Virtual Lab** — a browser-based test harness that lets product, QA, and integration teams configure virtual sites, FCC simulator profiles, pumps, nozzles, products, and callback targets, then exercise pre-auth and transaction flows without physical FCC hardware.

## What This System Does

1. Hosts a management API and Angular UI for configuring sites, profiles, forecourts, products, scenarios, and callback targets
2. Emulates FCC-facing APIs with site/profile-specific auth rules, payload formats, and sequence behavior
3. Simulates pump and nozzle operation, including normal dispense flows and pre-auth flows
4. Supports both transaction delivery styles: FCC push to callbacks and middleware pull from simulator endpoints
5. Preserves raw payloads, canonical payloads, correlation IDs, callback history, and event logs for every simulated action
6. Provides live visual operation of a virtual forecourt with SignalR-driven state updates
7. Supports deterministic seed/reset, export/import, and scenario replay for demos and regression testing
8. Optionally validates simulated payloads against middleware-facing canonical expectations

## Technology Stack

| Component | Technology |
|-----------|-----------|
| Frontend | Angular (standalone app) |
| Backend | ASP.NET Core Web API (.NET 10) |
| Realtime | SignalR |
| Persistence | EF Core with SQLite by default; keep path open for PostgreSQL/Azure SQL |
| Logging | Structured logs persisted in DB; console/App Insights sinks as needed |
| Testing | xUnit or NUnit for backend, Angular test stack for frontend, integration tests for push/pull/pre-auth flows |
| Hosting | Azure Static Web Apps for frontend, Azure Web App for backend |

## Intended Project Structure

```
VirtualLab/
├── src/
│   ├── VirtualLab.Api/             # REST API, FCC emulator endpoints, SignalR hubs
│   ├── VirtualLab.Application/     # Commands, queries, orchestration, scenario runner
│   ├── VirtualLab.Domain/          # Entities, enums, state machines, contracts
│   ├── VirtualLab.Infrastructure/  # EF Core, auth validators, payload engines, background jobs
│   └── VirtualLab.Tests/           # Backend unit/integration tests
└── ui/virtual-lab/                 # Angular app
    ├── src/app/core/              # API clients, SignalR client, shell
    ├── src/app/features/dashboard/
    ├── src/app/features/sites/
    ├── src/app/features/fcc-profiles/
    ├── src/app/features/forecourt-designer/
    ├── src/app/features/live-console/
    ├── src/app/features/preauth-console/
    ├── src/app/features/transactions/
    ├── src/app/features/logs/
    ├── src/app/features/scenarios/
    └── src/app/features/settings/
```

## Key Architecture Rules

1. **Profile-driven behavior first**: FCC variability should be data-driven where practical. Do not scatter vendor/profile conditionals across controllers and UI components.
2. **Observability is mandatory**: Every meaningful action should preserve raw payloads, canonical payloads when available, correlation IDs, timestamps, and structured event/log context.
3. **Deterministic replay matters**: Scenario runs and seeded flows should produce repeatable outcomes when configuration and seed are unchanged.
4. **Push and pull are both first-class**: Do not build only callback push and treat pull as an afterthought.
5. **Pre-auth variants are core requirements**: Both `CREATE_ONLY` and `CREATE_THEN_AUTHORIZE` must be represented in the state model and UI.
6. **Auth simulation is inbound-only**: The product has no end-user login UI. Simulate auth on FCC-facing and configured callback endpoints only.
7. **Do not lose debug context**: Store request/response headers and bodies, validation results, sequence decisions, and retry history where they matter.
8. **UI should expose integration reality**: Prefer payload inspectors, timelines, and live status over polished but shallow summaries.
9. **Keep persistence provider boundaries clean**: SQLite is fine for local/dev, but schema and queries should not assume it is the only future option.
10. **Avoid overengineering security**: Simulate FCC auth modes accurately, but do not spend time building user auth features that are explicitly out of scope.

## Simulation Guardrails

- A fresh seeded lab should be usable within 5 minutes of first startup in local development
- Dashboard load p95 with 10 sites, 100 pumps, 400 nozzles, and 10,000 transactions: <= 2 s
- Live forecourt actions reflected in UI via SignalR p95: <= 500 ms on local/dev deployment
- FCC emulator endpoints p95 on normal success paths: <= 300 ms excluding intentional simulation delays
- Transaction pull endpoint p95 for first page (`limit <= 100`) with 10,000 transactions: <= 250 ms
- Callback retry must not create duplicate transaction rows or duplicate success logs for the same attempt
- Raw payload, canonical payload, correlation ID, and event history must exist for every simulated transaction and pre-auth flow
- Deterministic replay must hold when using the same scenario seed and profile configuration

## Must-Read Artifacts (Before ANY Task)

| Artifact | Path | What It Tells You |
|----------|------|-------------------|
| Virtual Lab Development Plan | `docs/plans/dev-plan-virtual-lab.md` | Phase sequencing, task detail, guardrails, MVP priorities |
| Requirements | `Requirements.md` | Product requirements the simulator must model |
| High-Level Requirements | `HighLevelRequirements.md` | Cross-system flows and terminology alignment |
| Angular Portal Plan | `docs/plans/dev-plan-angular-portal.md` | Frontend conventions and useful screen/task patterns |
| Cloud Backend Plan | `docs/plans/dev-plan-cloud-backend.md` | Backend layering and service design conventions |
| Benchmark Guardrails | `VirtualLab/docs/benchmark-guardrails.md` | Phase 0 pass/fail thresholds, benchmark cases, deterministic replay rules |
| Smoke Validation Checklist | `VirtualLab/docs/smoke-validation-checklist.md` | Local and Azure smoke verification workflow |

## Core Domain Concepts

| Concept | Purpose |
|---------|---------|
| `LabEnvironment` | Top-level environment defaults, seed state, and callback presets |
| `Site` | A virtual station with one active FCC simulator configuration |
| `FccSimulatorProfile` | Auth mode, endpoints, payload templates, sequence rules, and capabilities |
| `Pump` | Visual and FCC-addressable forecourt unit |
| `Nozzle` | Product-bound dispensing outlet with status and numbering |
| `Product` | Fuel/product metadata including code, name, color, and price |
| `PreAuthSession` | Simulated reservation/authorization lifecycle |
| `SimulatedTransaction` | Generated transaction with raw/canonical payloads and delivery metadata |
| `CallbackTarget` | Destination configuration for push simulation |
| `LabEventLog` | Structured, filterable audit trail for simulation behavior |
| `ScenarioDefinition` | Deterministic scripted sequence for demo or regression flows |

## Required FCC Modes

### Auth Modes

- `NONE`
- `API_KEY`
- `BASIC_AUTH`

### Pre-Auth Modes

- `CREATE_ONLY`
- `CREATE_THEN_AUTHORIZE`

### Transaction Delivery Modes

- `PUSH`
- `PULL`
- `HYBRID`

## Primary API Surfaces

### Management API

- `GET /api/sites`
- `POST /api/sites`
- `PUT /api/sites/{id}`
- `GET /api/sites/{id}/forecourt`
- `PUT /api/sites/{id}/forecourt`
- `POST /api/sites/{id}/seed`
- `POST /api/sites/{id}/reset`
- `GET /api/fcc-profiles`
- `POST /api/fcc-profiles`
- `PUT /api/fcc-profiles/{id}`
- `GET /api/products`
- `GET /api/logs`
- `GET /api/transactions`
- `GET /api/scenarios`
- `POST /api/scenarios/run`

### FCC Emulator API

- `POST /fcc/{siteCode}/preauth/create`
- `POST /fcc/{siteCode}/preauth/authorize`
- `POST /fcc/{siteCode}/preauth/cancel`
- `GET /fcc/{siteCode}/transactions`
- `POST /fcc/{siteCode}/transactions/ack`
- `GET /fcc/{siteCode}/pump-status`
- `GET /fcc/{siteCode}/health`

### Lab Action API

- `POST /api/sites/{siteId}/pumps/{pumpId}/nozzles/{nozzleId}/lift`
- `POST /api/sites/{siteId}/pumps/{pumpId}/nozzles/{nozzleId}/hang`
- `POST /api/sites/{siteId}/pumps/{pumpId}/nozzles/{nozzleId}/dispense`
- `POST /api/sites/{siteId}/transactions/push`
- `POST /api/sites/{siteId}/preauth/simulate`
- `POST /api/sites/{siteId}/callbacks/test`

### Callback Capture API

- `POST /callbacks/{targetKey}`
- `GET /api/callbacks/{targetKey}/history`

## Logging Expectations

Use structured categories such as:

- `LabAction`
- `FccRequest`
- `FccResponse`
- `PreAuthSequence`
- `TransactionGenerated`
- `TransactionPushed`
- `TransactionPulled`
- `CallbackAttempt`
- `CallbackFailure`
- `AuthFailure`
- `StateTransition`
- `ScenarioRun`

Logs should support filtering by site, profile, severity, category, and correlation ID.

## Implementation Priorities

1. Build the backend state model and FCC profile engine before trying to make the UI look complete.
2. Preserve raw/canonical payload inspection from the first end-to-end flow.
3. Make the forecourt visual and interactive early, but never bypass the real backend state machine to do it.
4. Optimize for deterministic scenarios and debuggability rather than premature enterprise hardening.
5. Keep the first DOMS-like profile close enough to be useful for contract discussions, even if not every edge case is modeled initially.

## Testing Standards

- Backend unit tests for profile resolution, auth validation, state machines, transaction generation, and retry logic
- Backend integration tests for push, pull, callback capture, and pre-auth flows
- Frontend tests for management flows, live update wiring, and payload/log inspection surfaces
- Scenario-based regression tests for seeded demo flows
- Basic latency/seed-volume checks against the benchmark dataset defined in the plan
