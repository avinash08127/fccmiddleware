# Virtual Lab Pending Items

Scope scanned:
- `VirtualLab/src/VirtualLab.Api`
- `VirtualLab/src/VirtualLab.Application`
- `VirtualLab/src/VirtualLab.Infrastructure`
- `VirtualLab/src/VirtualLab.Tests`
- `VirtualLab/ui/virtual-lab`

Scan focus:
- explicit `TODO` / simplified markers
- partial implementations behind completed screens or APIs
- planned capabilities that are only backend-only or stubbed in UI
- determinism, benchmark, and simulator-fidelity gaps

Tooling note:
- `dotnet test VirtualLab/src/VirtualLab.Tests/VirtualLab.Tests.csproj` could not be run in this shell because `dotnet` is not installed (`/bin/bash: dotnet: command not found`).

---

### VL-PEND-001
- **Title:** Scenario replay is not truly deterministic or isolated across repeated runs
- **Module:** Scenarios
- **Severity:** High
- **Category:** Not yet implemented
- **Status:** PENDING
- **Description:** The scenario system records a `ReplaySeed` and `ReplaySignature`, but repeated runs are not isolated from prior runtime data. Transaction numbering is derived from the current stored transaction count per nozzle, so every prior run changes the next run's generated IDs. Pre-auth creation also still generates random IDs when the script does not supply one. On top of that, the replay signature hashes only scenario inputs (`scenarioKey`, seed, definition JSON), not the actual runtime outputs, so it cannot detect drift in generated transactions, callbacks, or pre-auth IDs.
- **Evidence:**
  - `VirtualLab/src/VirtualLab.Infrastructure/Scenarios/ScenarioService.cs:92-103` — replay signature is computed before execution from seed + definition only
  - `VirtualLab/src/VirtualLab.Infrastructure/Scenarios/ScenarioService.cs:1142-1145` — `ComputeReplaySignature(...)` hashes only `scenarioKey|replaySeed|definitionJson`
  - `VirtualLab/src/VirtualLab.Infrastructure/Scenarios/ScenarioService.cs:918-946` — setup can reset nozzle state and cancel active pre-auth sessions, but it does not clear prior transactions or completed pre-auth records
  - `VirtualLab/src/VirtualLab.Infrastructure/Forecourt/ForecourtSimulationService.cs:793-801` — transaction sequence uses `CountAsync(...) + 1`, so repeated runs shift generated transaction IDs
  - `VirtualLab/src/VirtualLab.Infrastructure/PreAuth/PreAuthSimulationService.cs:226-228` — create flow still generates a random `PA-...` ID with `Guid.NewGuid()`
  - `VirtualLab/src/VirtualLab.Tests/Api/ScenarioApiTests.cs:24-53` — current deterministic test only checks that two runs share the same replay signature, not that their generated outputs match
- **Impact:** The lab can report a scenario as deterministic while producing different transaction IDs, pre-auth IDs, and callback artifacts on each rerun. That weakens regression value and makes replay debugging less trustworthy.
- **Recommended Fix:** Run scenarios against an isolated runtime snapshot or clear scenario-generated artifacts before rerun, derive every generated ID from the replay seed, and compute an additional output signature from ordered generated artifacts so runtime drift is observable.

---

### VL-PEND-002
- **Title:** Benchmark harness measures synthetic in-memory work instead of real API and persistence hot paths
- **Module:** Diagnostics & Performance
- **Severity:** High
- **Category:** Simpler implementation
- **Status:** PENDING
- **Description:** The current latency probe does not exercise the seeded SQLite/SQL Server database, SignalR pipeline, or real emulator endpoints. It builds an in-memory synthetic dataset and measures LINQ operations over local collections. The benchmark script then calls only the diagnostics endpoint, so guardrail results can stay green while real queries or endpoints regress.
- **Evidence:**
  - `VirtualLab/src/VirtualLab.Application/Diagnostics/DiagnosticProbeService.cs:17-24` — latency summary is built from `SyntheticDataset`
  - `VirtualLab/src/VirtualLab.Application/Diagnostics/DiagnosticProbeService.cs:70-165` — all measurements are in-memory collection operations, not real EF Core or HTTP paths
  - `VirtualLab/scripts/run-benchmarks.mjs:25-40` — benchmark script samples only `/api/diagnostics/latency`
  - `VirtualLab/docs/benchmark-guardrails.md:45-65` — documented guardrails expect meaningful verification of dashboard, FCC, and transaction pull behavior against the real seeded lab
- **Impact:** Performance regressions in actual database queries, serialization, middleware, or transport paths can be missed. The benchmark currently provides confidence in synthetic calculations, not in production-like hot paths.
- **Recommended Fix:** Replace or supplement the synthetic probe with timed requests against real endpoints over a seeded database, and include direct measurement for `/api/dashboard`, `/api/sites`, `/fcc/{siteCode}/transactions`, and end-to-end SignalR update latency.

---

### VL-PEND-003
- **Title:** Product management is API-only; Angular UI has no product CRUD surface
- **Module:** Management UI
- **Severity:** Medium
- **Category:** Not yet implemented
- **Status:** PENDING
- **Description:** The backend exposes full product management endpoints, but the Angular app does not provide a products screen or client methods for create/update/archive. The UI only reads products to populate forecourt designer dropdowns.
- **Evidence:**
  - `VirtualLab/src/VirtualLab.Api/ManagementEndpoints.cs:121-146` — API exposes list/get/create/update/delete product routes
  - `VirtualLab/ui/virtual-lab/src/app/app.routes.ts:20-33` — no products route exists in the Angular application
  - `VirtualLab/ui/virtual-lab/src/app/core/services/lab-api.service.ts:986-989` — UI client exposes only `getProducts(...)`
  - `VirtualLab/ui/virtual-lab/src/app/features/forecourt-designer/forecourt-designer.component.ts:531-538` — products are loaded only as lookup data for nozzle assignment
- **Impact:** Users cannot manage products end-to-end from the Virtual Lab UI. Any correction to product code, price, grade, or activation state currently requires API tooling or direct data changes outside the intended operator flow.
- **Recommended Fix:** Add a products feature screen plus `createProduct`, `updateProduct`, and `archiveProduct` client methods, and wire it into the main navigation alongside sites and FCC profiles.

---

### VL-PEND-004
- **Title:** Settings screen is still a stub; environment lifecycle controls remain backend-only
- **Module:** Settings & Lifecycle Controls
- **Severity:** Medium
- **Category:** Not yet implemented
- **Status:** PENDING
- **Description:** The management API supports lab environment update, prune, export, and import operations, but the UI settings screen only displays three runtime strings. The Angular client also exposes only `getLabEnvironment()` and does not surface lifecycle actions from the browser.
- **Evidence:**
  - `VirtualLab/src/VirtualLab.Api/ManagementEndpoints.cs:13-50` — backend exposes lab environment read/update/prune/export/import routes
  - `VirtualLab/ui/virtual-lab/src/app/core/services/lab-api.service.ts:933-934` — UI client only fetches the environment summary
  - `VirtualLab/ui/virtual-lab/src/app/features/settings/settings.component.ts:1-22` — settings page is a static runtime-config readout
- **Impact:** Retention management, backup portability, and environment import/export exist only for API consumers. That leaves a visible top-level screen in the UI without the operational controls the backend already supports.
- **Recommended Fix:** Expand the settings area into an environment admin surface for retention settings, dry-run prune, export/import, and lifecycle configuration, backed by the existing management endpoints.

---

### VL-PEND-005
- **Title:** DOMS JPL unsolicited push notifications are simulated as polling side effects, not real pushes
- **Module:** Vendor Simulators
- **Severity:** Medium
- **Category:** Simpler implementation
- **Status:** PENDING
- **Description:** The DOMS JPL management API accepts a push-notification request and reports it as queued, but the simulator service does not actually deliver unsolicited frames to connected clients. Instead it logs that active clients will observe the change on their next poll.
- **Evidence:**
  - `VirtualLab/src/VirtualLab.Infrastructure/DomsJpl/DomsJplManagementEndpoints.cs:144-150` — management endpoint calls `SendUnsolicitedPushAsync(...)` and reports the notification as queued
  - `VirtualLab/src/VirtualLab.Infrastructure/DomsJpl/DomsJplSimulatorService.cs:492-501` — implementation explicitly says this is a simplified approach and does not track or push to connected client streams
- **Impact:** Integrations that depend on real unsolicited DOMS notifications cannot be validated accurately. The simulator understates push timing, client state handling, and connection-level behavior.
- **Recommended Fix:** Track active TCP client streams/sessions and emit properly framed unsolicited messages immediately, rather than relying on the next client poll to observe state changes.

---

### VL-PEND-006
- **Title:** Frontend automated test coverage is effectively absent for Virtual Lab features
- **Module:** Frontend Quality
- **Severity:** Medium
- **Category:** Not yet implemented
- **Status:** PENDING
- **Description:** The Angular app includes a test runner, but feature coverage has not been built out. The only current spec verifies that the root component instantiates. There are no component or integration tests for sites, profiles, forecourt designer, live console, pre-auth console, transactions, logs, scenarios, or settings.
- **Evidence:**
  - `VirtualLab/ui/virtual-lab/package.json:10-11` — frontend test tooling is configured via `ng test`
  - `VirtualLab/ui/virtual-lab/src/app/app.component.spec.ts:5-14` — only existing spec checks root component creation
  - Repository scan of `VirtualLab/ui/virtual-lab/src/app/**/*.spec.ts` returns only `VirtualLab/ui/virtual-lab/src/app/app.component.spec.ts`
- **Impact:** The most interaction-heavy surfaces in the Virtual Lab UI are unguarded. Refactors in routing, request wiring, live updates, payload inspectors, or management forms can regress without automated detection.
- **Recommended Fix:** Add focused component/service tests for management flows and live consoles first, then add a small number of end-to-end checks for seeded happy paths such as site/profile editing, pre-auth execution, transaction replay, and callback history replay.

---

### VL-PEND-007
- **Title:** PostgreSQL support is still documented as a future upgrade path, not a shipped provider
- **Module:** Persistence & Deployment
- **Severity:** Low
- **Category:** Not yet implemented
- **Status:** PENDING
- **Description:** The deployment documentation describes PostgreSQL as a later upgrade path, but the infrastructure layer currently supports only SQLite and SQL Server. Shared environments that want PostgreSQL remain blocked on provider wiring and migration validation.
- **Evidence:**
  - `VirtualLab/src/VirtualLab.Infrastructure/DependencyInjection.cs:46-68` — only `Sqlite` and `SqlServer` are accepted providers
  - `VirtualLab/docs/azure-deployment.md:101-108` — deployment guide explicitly states that the codebase does not yet ship the Npgsql EF Core provider
- **Impact:** Teams that standardize on PostgreSQL cannot use the documented path without additional engineering work. The current deployment story is narrower than the docs imply at a glance.
- **Recommended Fix:** Add `Npgsql.EntityFrameworkCore.PostgreSQL`, wire provider selection in DI, and validate migrations/schema behavior against PostgreSQL before documenting it as a ready option.

