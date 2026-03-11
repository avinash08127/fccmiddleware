# FCC Simulator + Pump Simulator Virtual Lab — Phased Development Plan

**Agent System Prompt:** Always prepend `docs/plans/agent-prompt-virtual-lab.md` when assigning any task below.

**Sprint Cadence:** 2-week sprints

## Simulation Guardrails

These constraints should shape implementation decisions from the start rather than being deferred to hardening.

- A fresh seeded lab should be usable within 5 minutes of first startup in local development
- Site dashboard load p95 with 10 sites, 100 pumps, 400 nozzles, and 10,000 transactions: <= 2 s
- Live forecourt actions (`lift`, `hang`, `dispense`) reflected in UI via SignalR p95: <= 500 ms on local/dev deployment
- FCC emulator endpoints (`/fcc/...`) p95 for normal success paths on local/dev data volumes: <= 300 ms excluding intentional delay simulation
- Transaction pull endpoint p95 for first page (`limit <= 100`) with 10,000 stored simulated transactions: <= 250 ms
- Callback retry scheduling must not create duplicate transaction records or duplicate success logs for the same delivery attempt
- Every simulated request/response pair must preserve raw payload, canonical payload when produced, correlation ID, and timestamped event history
- Scenario replay must be deterministic when run with the same seed and profile configuration
- The system must support at least these auth simulations per site/profile: `NONE`, `API_KEY`, `BASIC_AUTH`
- The first implementation must support both pre-auth modes: `CREATE_ONLY` and `CREATE_THEN_AUTHORIZE`

---

## Phase 0 — Foundations (Sprints 1–2)

### VL-0.0: Guardrails, Architecture Baseline, and Benchmark Harness

**Sprint:** 1
**Prereqs:** None
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `Requirements.md` — REQ-3, REQ-5, REQ-6, REQ-7, REQ-8, REQ-9, REQ-10, REQ-12, REQ-14
- `HighLevelRequirements.md` — §§3, 5, 6, 7, 8, 9, 10, 12, 15
- `docs/plans/dev-plan-virtual-lab.md` — this plan, especially the guardrails above

**Task:**
Turn the virtual lab goals into measurable design constraints, baseline seed volumes, and repeatable validation checks.

**Detailed instructions:**
1. Convert the guardrails above into explicit benchmark and verification cases for:
   - seeded lab startup readiness
   - dashboard/site load latency
   - SignalR live update latency
   - FCC emulator endpoint latency
   - transaction pull query latency
   - deterministic replay
2. Define the default benchmark seed shape:
   - 10 sites
   - 10 pumps per site
   - 4 nozzles per pump
   - 10,000 transactions across mixed push/pull and pre-auth scenarios
3. Add a lightweight benchmark or diagnostics harness for backend query timing and API timing
4. Document the pass/fail thresholds so later tasks can reference them
5. Add a validation checklist for local and Azure-hosted smoke runs

**Acceptance criteria:**
- Guardrails are documented in the repo and referenced by later tasks
- A representative seed size is defined and reusable
- At least one repeatable latency check is runnable locally before Phase 2 begins
- Deterministic replay expectations are explicitly documented

**Baseline artifacts produced by this task:**
- `VirtualLab/docs/benchmark-guardrails.md`
- `VirtualLab/docs/smoke-validation-checklist.md`
- `VirtualLab/config/benchmark-seed.json`
- `VirtualLab/scripts/run-benchmarks.mjs`

### VL-0.1: Solution and Application Scaffolding

**Sprint:** 1
**Prereqs:** None
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — architecture, API surface, UI plan
- `docs/plans/dev-plan-angular-portal.md` — Angular planning conventions and screen/task structure
- `docs/plans/dev-plan-cloud-backend.md` — backend planning conventions and layering expectations
- `VirtualLab/docs/benchmark-guardrails.md` — benchmark seed and pass/fail guardrails to preserve in scaffolding
- `VirtualLab/docs/smoke-validation-checklist.md` — local/Azure smoke workflow that the scaffold must support

**Task:**
Create the .NET and Angular solution scaffold for the virtual lab and establish the baseline development workflow.

**Detailed instructions:**
1. Create the backend solution structure:
   - `VirtualLab.Api`
   - `VirtualLab.Application`
   - `VirtualLab.Domain`
   - `VirtualLab.Infrastructure`
   - `VirtualLab.Tests`
2. Create the Angular app scaffold with standalone components and feature folders for:
   - dashboard
   - sites
   - FCC profiles
   - forecourt designer
   - live pump console
   - pre-auth console
   - transactions
   - logs
   - scenarios
   - settings
3. Configure local development so Angular can call the backend cleanly via proxy or environment config
4. Add baseline tooling:
   - backend formatting and analyzer config
   - frontend lint/test/build config
   - environment-specific configuration placeholders
5. Add CI skeleton steps for backend build/test and frontend build/test

**Acceptance criteria:**
- Angular app builds successfully
- .NET solution builds successfully
- Local development can run frontend and backend together
- Project structure matches the intended architecture with no placeholder ambiguity

### VL-0.2: Domain Model and Persistence Baseline

**Sprint:** 1
**Prereqs:** VL-0.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — conceptual model and FCC profile strategy
- `Requirements.md` — relevant REQ items for sites, profiles, pre-auth, transactions, logging
- `VirtualLab/docs/benchmark-guardrails.md` — default seed profile and hot-path thresholds for indexes and queries

**Task:**
Implement the core persistence model for the virtual lab so every later workflow has a stable data contract.

**Detailed instructions:**
1. Model and persist these core entities:
   - `LabEnvironment`
   - `Site`
   - `FccSimulatorProfile`
   - `Pump`
   - `Nozzle`
   - `Product`
   - `PreAuthSession`
   - `SimulatedTransaction`
   - `CallbackTarget`
   - `CallbackAttempt`
   - `LabEventLog`
   - `ScenarioDefinition`
   - `ScenarioRun`
2. Capture enough structure to support:
   - per-site auth config
   - push/pull/hybrid delivery mode
   - raw and canonical payload storage
   - deterministic scenario replay
   - timeline/correlation inspection
3. Add EF Core configuration, initial migration, and indexes for:
   - site/profile lookup
   - transaction listing and correlation search
   - log filtering by category, site, timestamp
   - callback history
4. Use SQLite by default for local/dev; keep provider boundaries clean for later PostgreSQL support
5. Add repository or query-service abstractions only where they simplify application logic; avoid unnecessary indirection

**Acceptance criteria:**
- Initial migration creates all core tables successfully
- One site with multiple pumps/nozzles/products can be created and queried
- Raw payloads, canonical payloads, and log records persist correctly
- Transaction and log queries are indexed for the defined hot paths

### VL-0.3: Seed Data and Demo Fixtures

**Sprint:** 2
**Prereqs:** VL-0.2
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — target outcomes, initial profiles, suggested MVP cut

**Task:**
Create a default seeded environment so the lab is immediately usable for demos and validation.

**Detailed instructions:**
1. Seed default products, sites, pumps, nozzles, and callback targets
2. Seed at least these initial FCC profiles:
   - DOMS-like profile
   - Generic create-only profile
   - Generic create-then-authorize profile
   - Bulk push profile
3. Ensure the profile set covers:
   - `NONE`
   - `API_KEY`
   - `BASIC_AUTH`
4. Add a seed/reset command or startup path for local/dev environments
5. Document which site/profile combinations are the default demo paths

**Acceptance criteria:**
- Fresh startup provides at least one ready-to-demo site
- Seed data covers both pre-auth modes and all supported auth modes
- Seed/reset behavior is repeatable and documented

---

## Phase 1 — Simulation Core (Sprints 2–4)

### VL-1.1: FCC Profile Engine and Contract Model

**Sprint:** 2
**Prereqs:** VL-0.2
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — FCC profile strategy, profile contract, initial profiles
- `Requirements.md` — integration and auth-related requirements

**Task:**
Build the profile-driven engine that determines how each FCC simulator behaves without hardcoding all variants into endpoint logic.

**Detailed instructions:**
1. Define the profile contract model with fields for:
   - endpoint surface
   - auth mode and credentials
   - push/pull capability
   - pre-auth mode
   - request/response templates
   - validation rules
   - field mappings
   - simulated delay/failure toggles
2. Implement profile resolution by site so emulator endpoints can dispatch behavior through the active profile
3. Keep profile behavior data-driven where practical; use code extension points only for cases templates cannot express cleanly
4. Validate profile completeness on create/update so broken profiles cannot be activated
5. Add sample payload rendering helpers and profile preview/test utilities

**Acceptance criteria:**
- Profile configuration drives common behavior without code changes
- Invalid or incomplete profiles are rejected with useful validation output
- Profile resolution is centralized and reusable by emulator, scenario, and UI layers

### VL-1.2: FCC Auth Simulation and Endpoint Middleware

**Sprint:** 2
**Prereqs:** VL-1.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — authentication simulation plan

**Task:**
Implement inbound auth simulation for FCC-facing endpoints and configured callback capture endpoints.

**Detailed instructions:**
1. Add auth validation for supported modes:
   - `NONE`
   - `API_KEY`
   - `BASIC_AUTH`
2. Make auth behavior configurable per site/profile and callback target
3. Apply auth enforcement only to FCC/emulator and configured callback endpoints, not to the management UI/API
4. Emit warning log entries for every auth failure with safe request metadata
5. Ensure logs and UI filtering can surface `AuthFailure` quickly

**Acceptance criteria:**
- FCC endpoints enforce per-profile auth rules correctly
- Callback endpoints can optionally enforce configured auth
- Failed auth attempts create structured warning logs
- Management API remains open for local/dev as designed

### VL-1.3: Pre-Auth State Machine and Emulator Endpoints

**Sprint:** 3
**Prereqs:** VL-1.1, VL-1.2
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — pre-auth modes, emulator API, conceptual model

**Task:**
Implement the full pre-auth simulation flow with profile-specific sequencing and timeline visibility.

**Detailed instructions:**
1. Model and enforce the pre-auth state machine:
   - `PENDING`
   - `AUTHORIZED`
   - `DISPENSING`
   - `COMPLETED`
   - `CANCELLED`
   - `EXPIRED`
   - `FAILED`
2. Implement FCC emulator endpoints:
   - `POST /fcc/{siteCode}/preauth/create`
   - `POST /fcc/{siteCode}/preauth/authorize`
   - `POST /fcc/{siteCode}/preauth/cancel`
3. Support both modes:
   - `CREATE_ONLY`
   - `CREATE_THEN_AUTHORIZE`
4. Capture raw requests, responses, validation errors, and sequence transitions in logs
5. Implement expiry handling and failure injection hooks

**Acceptance criteria:**
- Create-only profiles authorize correctly during create
- Create-then-authorize profiles reject incomplete or out-of-order flows
- Cancel, expire, and failure paths are implemented and logged
- A full timeline exists for each pre-auth session

### VL-1.4: Normal Transaction Generator and Nozzle State Engine

**Sprint:** 3
**Prereqs:** VL-0.3, VL-1.1
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — conceptual model, live pump console, target outcomes

**Task:**
Simulate realistic nozzle activity and generate transactions from virtual forecourt actions.

**Detailed instructions:**
1. Model nozzle states and allowed transitions for:
   - idle
   - lifted
   - authorized
   - dispensing
   - hung
   - faulted
2. Implement lab action handlers for:
   - lift
   - hang
   - dispense/start-stop behavior
3. Generate simulated transactions with:
   - deterministic IDs when seeded
   - site/pump/nozzle/product context
   - raw FCC payload
   - canonical payload placeholder or generated mapping output
   - event timeline and correlation ID
4. Support configurable flow rates, amount/volume targets, and duplicate injection toggles
5. Ensure generated transactions integrate with both push and pull delivery paths

**Acceptance criteria:**
- Nozzle actions produce realistic state transitions and transactions
- Transactions preserve both raw and canonical inspection paths
- Duplicate and failure injection toggles are supported without corrupting baseline flows

### VL-1.5: Push/Pull Delivery Engine and Callback Retry

**Sprint:** 4
**Prereqs:** VL-1.4
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — delivery modes, emulator API, callback capture API

**Task:**
Support transaction delivery through FCC pull endpoints and push callbacks, with acknowledgement, retry, and observability.

**Detailed instructions:**
1. Implement FCC emulator endpoints:
   - `GET /fcc/{siteCode}/transactions`
   - `POST /fcc/{siteCode}/transactions/ack`
   - `GET /fcc/{siteCode}/pump-status`
   - `GET /fcc/{siteCode}/health`
2. Implement transaction delivery modes:
   - `PUSH`
   - `PULL`
   - `HYBRID`
3. Implement callback dispatch with:
   - request persistence
   - response persistence
   - retry scheduling
   - acknowledgement tracking
4. Prevent duplicate success recording across retries for the same attempt
5. Emit structured logs for delivery success, failure, retry, and acknowledgement outcomes

**Acceptance criteria:**
- External systems can pull transactions from the simulator
- Simulator can push transactions to callback targets with retry support
- Callback retry behavior is observable and idempotent from a storage/logging perspective
- Health and pump-status endpoints are available for test harnesses and UI use

---

## Phase 2 — Management and Visual UI (Sprints 4–6)

### VL-2.1: Management API for Sites, Profiles, Products, and Seeds

**Sprint:** 4
**Prereqs:** VL-0.3, VL-1.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — management API, conceptual model, settings screen

**Task:**
Implement the management API that powers the Angular application and developer workflows.

**Detailed instructions:**
1. Implement CRUD or management endpoints for:
   - sites
   - FCC profiles
   - products
   - forecourt configuration
   - seed/reset actions
2. Support site duplication/template creation for fast lab setup
3. Validate site/profile compatibility before save
4. Return view models tailored for UI screens where that materially reduces frontend complexity
5. Keep response shapes consistent enough to support future scenario export/import

**Acceptance criteria:**
- Angular screens can manage sites, profiles, products, and seed/reset flows through supported APIs
- Invalid mappings are rejected with actionable validation messages
- Site duplication/template-style setup is supported

### VL-2.2: Dashboard and Site/Profile Management Screens

**Sprint:** 5
**Prereqs:** VL-2.1
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — dashboard, sites, and FCC profiles UI plan
- `docs/plans/dev-plan-angular-portal.md` — frontend implementation conventions

**Task:**
Build the first usable Angular screens for environment overview and core configuration management.

**Detailed instructions:**
1. Implement the dashboard with:
   - site summary cards
   - active transactions
   - auth failures
   - callback success/failure indicators
   - recent logs/alerts
2. Build sites management screens:
   - create/edit/delete
   - assign profile
   - configure transaction mode and callback target
   - configure fiscalization flags
   - duplicate from template
3. Build FCC profile management screens:
   - choose auth mode
   - choose pre-auth mode
   - edit endpoints and payload templates
   - import/export profile JSON
4. Make validation and error display explicit; do not hide broken mappings behind silent defaults

**Acceptance criteria:**
- Users can configure sites and profiles end-to-end from the UI
- Dashboard surfaces meaningful current-state information without manual refresh
- Validation failures are visible and actionable

### VL-2.3: Forecourt Designer

**Sprint:** 5
**Prereqs:** VL-2.1
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — forecourt designer UI plan

**Task:**
Build the visual editor for pumps, nozzles, numbering, product mappings, and layout persistence.

**Detailed instructions:**
1. Implement a visual layout editor using SVG or another approach that scales cleanly for live state overlays
2. Support:
   - add/remove pumps
   - add/remove nozzles
   - drag/drop positioning
   - display number vs FCC number mapping
   - product assignment
3. Persist site layout and mappings
4. Keep editing fast: inline edits, cloning, and bulk pump creation where reasonable
5. Ensure the data model can round-trip exactly without losing numbering or product mappings

**Acceptance criteria:**
- Users can build and save a forecourt visually
- Display numbering and FCC numbering are configurable separately
- Layout and mappings persist and reload accurately

### VL-2.4: Live Pump Console and Pre-Auth Console

**Sprint:** 6
**Prereqs:** VL-1.3, VL-1.4, VL-2.3
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — live pump console and pre-auth console UI plan

**Task:**
Expose the simulation core through interactive operator screens with live status updates.

**Detailed instructions:**
1. Build the live pump console with controls for:
   - lift nozzle
   - start/stop dispense
   - hang nozzle
   - target amount/volume
   - failure injection
2. Build the pre-auth console with controls for:
   - create-only pre-auth
   - create-then-authorize pre-auth
   - customer tax fields
   - cancel/expire simulation
   - sequence timeline display
3. Wire live updates through SignalR
4. Make transaction generation and state transitions visible in real time

**Acceptance criteria:**
- Users can operate nozzles and pre-auth flows from the UI
- Live state changes appear without page refresh
- Failure injection and timelines are visible and usable

### VL-2.5: Transactions and Logs Screens

**Sprint:** 6
**Prereqs:** VL-1.5, VL-2.2
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — transactions screen, logs screen, logging and observability plan

**Task:**
Build the core observability screens so simulated integrations are inspectable rather than opaque.

**Detailed instructions:**
1. Build the transactions screen with:
   - list/filter/search
   - raw payload view
   - canonical payload view
   - manual replay/re-push controls
2. Build the logs screen with:
   - live tail
   - site/profile/severity/category filters
   - correlation ID search
   - request/response and headers inspector
   - export as JSON
3. Make the transaction timeline assemble cleanly from logs and domain events
4. Ensure performance remains within the list/query guardrails on seeded datasets

**Acceptance criteria:**
- Transactions and logs are inspectable without refresh
- Correlation search and payload inspection work end-to-end
- Manual replay/re-push actions are available from the transaction view

---

## Phase 3 — Scenarios and Contract Validation (Sprints 6–7)

### VL-3.1: Callback Capture and Replay

**Sprint:** 6
**Prereqs:** VL-1.5, VL-2.5
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — callback capture API and integration scenario goals

**Task:**
Allow the lab to receive, inspect, and replay callback traffic for self-contained demos and debugging.

**Detailed instructions:**
1. Implement:
   - `POST /callbacks/{targetKey}`
   - `GET /api/callbacks/{targetKey}/history`
2. Persist callback headers, bodies, auth outcome, response status, correlation metadata, and timestamps
3. Add replay support from the UI and API
4. Preserve original payload and correlation context during replay while clearly marking replayed attempts

**Acceptance criteria:**
- Callback history is stored and queryable
- Users can replay captured callbacks
- Replay preserves correlation metadata while distinguishing original vs replay attempts

### VL-3.2: Scenario Runner and Scenario Library

**Sprint:** 7
**Prereqs:** VL-1.3, VL-1.4, VL-1.5, VL-2.4
**Estimated effort:** 3 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — scenarios screen, recommended scenarios, target outcomes

**Task:**
Implement reusable, deterministic scenario execution for demos, regressions, and integration walkthroughs.

**Detailed instructions:**
1. Define a scenario format that can express:
   - seed/setup
   - action sequence
   - simulated delays/failures
   - expected outcomes/assertions
2. Implement scenario execution and result recording
3. Seed a baseline scenario library including:
   - fiscalized pre-auth success
   - create-then-authorize timeout
   - bulk push duplicate batch
   - offline pull catch-up
4. Expose scenario run results in the UI and logs
5. Support import/export as JSON

**Acceptance criteria:**
- Scenarios can create data, perform actions, and verify outcomes
- Results are visible in UI and logs
- Scenario import/export and deterministic rerun work

### VL-3.3: Middleware Contract and Canonical Payload Validation

**Sprint:** 7
**Prereqs:** VL-1.1, VL-2.5
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `Requirements.md` — integration requirements
- Relevant canonical schemas and payload contracts already used elsewhere in the repo

**Task:**
Add optional contract validation so the lab can highlight whether simulated FCC traffic maps cleanly into middleware expectations.

**Detailed instructions:**
1. Add canonical validation for simulated transaction and pre-auth payload paths
2. Surface missing or invalid required fields during inspection and scenario runs
3. Add baseline validation rules for the DOMS-like profile first
4. Keep validation optional and diagnostic-focused; do not block all simulation flows when comparison rules are incomplete

**Acceptance criteria:**
- Raw and canonical payloads can be compared in a meaningful way
- Missing required fields are highlighted during inspection
- DOMS-like profile has baseline canonical validation coverage

---

## Phase 4 — Hardening and Azure Readiness (Sprints 7–8)

### VL-4.1: Structured Logging, Telemetry, and Retention Controls

**Sprint:** 7
**Prereqs:** VL-2.5
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — logging and observability plan

**Task:**
Harden observability and add lifecycle controls so long-running demo environments remain usable.

**Detailed instructions:**
1. Finalize structured log categories and severity usage
2. Add retention and pruning controls for logs, callback history, and old transactions
3. Add export/import support for environment-level backup and demo portability
4. Ensure pruning does not break timelines or scenario history unexpectedly
5. Add backend telemetry hooks suitable for Azure-hosted environments

**Acceptance criteria:**
- Log and data retention are configurable
- Export/import works for demos and backups
- Long-running environments can be pruned without manual DB intervention

### VL-4.2: Azure Deployment Packaging and Environment Configuration

**Sprint:** 8
**Prereqs:** VL-0.1, VL-4.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — Azure deployment section

**Task:**
Prepare the virtual lab for Azure deployment with clear environment boundaries and reproducible builds.

**Detailed instructions:**
1. Prepare Angular deployment for Azure Static Web Apps
2. Prepare ASP.NET Core deployment for Azure Web App
3. Document required app settings and environment variables
4. Keep SQLite usable for local/dev while documenting upgrade path for PostgreSQL or Azure SQL in shared environments
5. Ensure frontend and backend can deploy independently

**Acceptance criteria:**
- Production builds succeed for frontend and backend
- Azure environment configuration is documented
- API and UI can deploy independently without hidden coupling

### VL-4.3: Automated Test Coverage and Demo Readiness

**Sprint:** 8
**Prereqs:** VL-3.2, VL-3.3, VL-4.2
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/plans/dev-plan-virtual-lab.md` — MVP cut, risks, implementation priorities

**Task:**
Add the automated coverage and demo assets required to treat the lab as a dependable integration tool rather than a prototype.

**Detailed instructions:**
1. Add backend unit tests for:
   - profile resolution
   - auth validation
   - pre-auth sequencing
   - transaction generation
   - callback retry behavior
2. Add frontend tests for:
   - core management flows
   - live update integration points
   - payload/log inspectors
3. Add end-to-end or integration coverage for:
   - push flow
   - pull flow
   - create-only pre-auth
   - create-then-authorize pre-auth
4. Document a demo script based on the seeded environment and scenario library

**Acceptance criteria:**
- Core simulation paths are covered by automated tests
- Demo flow is documented and reproducible
- Test coverage includes both success and failure/timeout paths

---

## Implementation Priorities

1. Keep simulator behavior data-driven where possible, especially FCC profile variability.
2. Preserve observability from day one: raw payloads, canonical views, logs, and correlation IDs are not optional polish.
3. Make the lab visually useful early, but do not let the UI outpace the backend state model.
4. Optimize for deterministic replay and debuggability over premature production-style hardening.
5. Treat push, pull, and pre-auth as first-class flows in the MVP, not post-MVP extras.

## Suggested MVP Cut

If the fastest usable cut is needed, target:

1. Multi-site environment with seeded demo data
2. DOMS-like profile plus one generic create-then-authorize profile
3. `API_KEY` and `BASIC_AUTH` simulation
4. Visual forecourt designer
5. Live nozzle actions and transaction generation
6. Pre-auth create and authorize flows
7. Push callbacks and transaction pull endpoints
8. Logs plus raw/canonical payload inspection

## Risks and Decisions to Lock Early

1. Decide whether FCC payload variability is expressed primarily through JSON templates, strategy classes, or a hybrid.
2. Decide whether the first shared Azure environment uses SQLite or a server database.
3. Decide whether the forecourt visualization is SVG-first; that is the safer choice for live state overlays and connectors.
4. Decide how faithfully the first DOMS-like profile mirrors the real contract versus a simplified approximation.
5. Decide whether callback targets are external-only or whether the lab hosts self-loop targets by default.
