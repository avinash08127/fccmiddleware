# Testing Strategy

## 1. Output Location
- **Target file:** `docs/specs/testing/testing-strategy.md`
- **Why:** `docs/STRUCTURE.md` maps "Testing strategy" to `/docs/specs/testing`.

## 2. Scope
- **TODO item:** 3.4 Testing Strategy
- **In scope:** Unit testing frameworks and coverage targets, integration testing approach, FCC simulator design, offline scenario testing, load/performance testing, contract testing
- **Out of scope:** Test data generation strategy (separate concern), CI/CD pipeline integration (covered by 3.3), observability/monitoring (covered by 3.5)

## 3. Source Traceability
- **Requirements:** REQ-4 (connectivity modes/offline), REQ-15 (Edge Agent), REQ-16 (error handling/retry)
- **HLD sections:** Cloud Backend section 5.3 (project structure `/tests/`), Edge Agent section 5.3 (project structure `test/`, `androidTest/`, `tools/fcc-simulator/`), Angular Portal (`e2e/`)
- **Assumptions:** Tier 1 and Tier 2 artefacts define the contracts and models under test. TODO 3.3 (CI/CD) defines where tests execute in pipelines.

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| k6 for load testing | JS-based scripting, CLI-friendly for CI, lightweight, strong HTTP/REST support | Cloud team writes k6 scripts; no JVM overhead in CI runners |
| Pact (JVM + .NET providers) for contract testing | Mature bi-directional contract testing; supports both Kotlin consumer and .NET provider | Pact broker required (hosted or self-hosted); contract artifacts checked into CI |
| FCC Simulator as standalone Kotlin process | Reusable across unit tests (in-process), integration tests, and manual dev; matches Edge Agent language | Lives in `tools/fcc-simulator/`; exposes HTTP/TCP matching DOMS protocol |
| Network fault injection via iptables/toxiproxy in Docker Compose | Reproducible, CI-compatible, no physical network manipulation | Offline scenario tests require Docker Compose harness |
| Coverage threshold: 80% line coverage for domain/adapter logic | Balances quality with pragmatism; UI and infrastructure layers excluded from hard target | CI gates on coverage for domain projects only |

## 5. Detailed Specification

### 5.1 Unit Testing

| Component | Framework | Mock Library | Coverage Target | Scope |
|---|---|---|---|---|
| Cloud Backend (.NET) | xUnit | NSubstitute | 80% line on `Domain`, `Application`, `Adapters.*` projects | Domain logic, normalization, dedup, state transitions, validation, adapter parsing |
| Edge Agent (Kotlin) | JUnit 5 | MockK | 80% line on `adapter`, `buffer`, `sync`, `preauth`, `ingestion` packages | FCC adapter parsing, buffer CRUD, sync orchestration, pre-auth flow, connectivity state machine |
| Angular Portal | Jasmine + Karma | Angular TestBed | 60% line on `features/` and `core/` | Component rendering, service methods, guard/interceptor logic |

**Exclusions from coverage gates:** generated code, DTOs with no logic, startup/DI configuration, Android `Activity`/`Fragment` lifecycle (tested via instrumented tests instead).

### 5.2 Integration Testing

| Layer | Tool | What It Tests | Data Setup |
|---|---|---|---|
| Cloud repositories + APIs | Testcontainers (PostgreSQL, Redis) | EF Core queries, dedup logic against real DB, API endpoint responses, event publishing | Per-test schema migration + seed via EF Core `EnsureCreated` |
| Cloud adapter (DOMS) | xUnit + sample payloads | Full normalization pipeline from raw DOMS JSON/XML to canonical model | Static fixture files in `tests/FccMiddleware.Adapter.Doms.Tests/Fixtures/` |
| Edge Agent Room DB | Room in-memory DB (`Room.inMemoryDatabaseBuilder`) | DAO queries, retention cleanup, buffer overflow, schema migrations | In-memory per test |
| Edge Agent Android | Robolectric | Foreground service lifecycle, connectivity manager, WorkManager scheduling | Robolectric shadows |
| Cross-component (E2E) | Docker Compose test harness | Edge Agent upload → Cloud ingest → verify stored → Odoo poll → acknowledge | See section 5.5 |

### 5.3 FCC Simulator

| Attribute | Specification |
|---|---|
| Location | `tools/fcc-simulator/` (Kotlin, standalone JAR) |
| Protocol | HTTP REST mimicking DOMS endpoints (transaction list, pre-auth, pump status, heartbeat) |
| Modes | **Static:** return canned responses from JSON fixtures. **Generator:** produce N transactions at configurable rate with randomized pump/nozzle/amount. **Error:** return configurable HTTP errors, timeouts, malformed payloads. |
| Configuration | YAML config file: `port`, `mode`, `transactionsPerMinute`, `errorRate`, `errorTypes[]`, `latencyMs`, `fixtures/` path |
| In-process use | Embeddable via `FccSimulator.start(config)` for JUnit 5 `@BeforeAll` in Edge Agent tests |
| CI use | `docker run fcc-simulator:latest --config ci-config.yaml` |
| Endpoints | `GET /api/transactions?since={cursor}`, `POST /api/preauth`, `GET /api/pumps/status`, `GET /api/heartbeat` |

### 5.4 Offline Scenario Testing

| Scenario ID | Description | Fault Injection | Assertion |
|---|---|---|---|
| OFF-1 | Internet drops during Edge→Cloud upload | Toxiproxy cuts cloud endpoint mid-batch | Partial batch not committed; Edge retries full batch; no duplicates after recovery |
| OFF-2 | FCC LAN drops during Edge poll | Toxiproxy cuts FCC simulator endpoint | ConnectivityState transitions to FCC_UNREACHABLE; alert raised; resumes polling on recovery |
| OFF-3 | Recovery after 1-hour internet outage | Toxiproxy restores after 1hr simulated clock | All buffered transactions uploaded in chronological order; dedup successful |
| OFF-4 | Recovery after 1-day outage (1,000 txns buffered) | Seed 1,000 buffered records, restore connectivity | Full replay completes without OOM; all records reach SYNCED status |
| OFF-5 | Recovery after 7-day outage (30,000 txns buffered) | Seed 30,000 buffered records, restore connectivity | Replay completes; memory stays below 256MB; battery drain < 5% on Urovo i9100 |
| OFF-6 | Simultaneous internet + FCC drop then staggered recovery | Toxiproxy cuts both, restores FCC first, then internet | Agent transitions through FULLY_OFFLINE → FCC_UNREACHABLE → INTERNET_DOWN → FULLY_ONLINE correctly |
| OFF-7 | Primary agent crash with healthy warm standby | Kill primary process, keep standby reachable on LAN | Standby promotes within 30s, localhost facade continues serving Odoo, no duplicate FCC command execution |
| OFF-8 | Stale former primary returns after failover | Restore old primary after a higher epoch is active | Returning node rejoins as `RECOVERING`; stale authoritative writes are rejected by leader epoch fencing |

**Execution environment:** Docker Compose locally and in CI. OFF-5 also runs on physical Urovo i9100 during Phase 6 hardening.

**Tooling:** Docker Compose with Toxiproxy sidecar. Compose file defines `cloud-api`, `postgres`, `redis`, `fcc-simulator`, `toxiproxy` services. Test runner (JUnit 5 or shell script) controls Toxiproxy via its admin API.

### 5.5 Cross-Component E2E Test Harness

**Architecture:** Docker Compose orchestrates Cloud API + PostgreSQL + Redis + FCC Simulator + Toxiproxy. Edge Agent logic runs as a headless JVM test client (extracted sync/upload modules, not full Android APK) or via Android emulator in CI.

**Scenarios:**
1. FCC Simulator generates transactions → Edge Agent polls and buffers → Edge uploads to Cloud → Cloud stores → Odoo poll API returns transactions → Odoo acknowledges → verify SYNCED_TO_ODOO
2. Pre-auth: Edge receives pre-auth → forwards to FCC Simulator → stores record → uploads to Cloud → Cloud stores pre-auth record
3. Offline replay: same as OFF-3/OFF-4 but verified end-to-end through Odoo acknowledge
4. Multi-agent failover: desktop + two Android agents run in parallel → primary crash or network partition → verify promotion, epoch fencing, and continuity of localhost behavior on Android HHTs

### 5.6 Load / Performance Testing

| Target | Tool | Scenario | Pass Criteria |
|---|---|---|---|
| Cloud ingestion throughput | k6 | Ramp to 100 req/sec (≈2M txns/day sustained) against `POST /api/v1/transactions/ingest` | p95 latency < 500ms; 0% HTTP 5xx; no DB connection pool exhaustion |
| Cloud Odoo poll | k6 | 50 concurrent Odoo instances polling `GET /api/v1/transactions` with filtering | p95 latency < 1s; correct pagination |
| Cloud batch upload | k6 | 200 Edge Agents uploading 50-txn batches every 30s | p95 latency < 2s; dedup works correctly under concurrent load |
| Edge Agent replay | Android instrumented test on Urovo i9100 | Replay 30,000 buffered transactions | Completes within 2 hours; peak memory < 256MB; battery drain < 5% over replay duration |

**Execution:** k6 scripts in `tests/load/`. Run in CI against `dev` environment on schedule (nightly). Run against `staging` before release.

### 5.7 Contract Testing

| Contract | Consumer | Provider | Tool | Artifact |
|---|---|---|---|---|
| Edge Agent → Cloud Upload API | Edge Agent (Kotlin, Pact JVM) | Cloud Backend (.NET, PactNet) | Pact | Pact JSON contract |
| Edge Agent → Cloud Pre-Auth API | Edge Agent (Kotlin) | Cloud Backend (.NET) | Pact | Pact JSON contract |
| Edge Agent → Cloud Config API | Edge Agent (Kotlin) | Cloud Backend (.NET) | Pact | Pact JSON contract |
| Edge Agent → Cloud Telemetry API | Edge Agent (Kotlin) | Cloud Backend (.NET) | Pact | Pact JSON contract |
| Edge Agent → Cloud Registration API | Edge Agent (Kotlin) | Cloud Backend (.NET) | Pact | Pact JSON contract |
| Odoo → Cloud Poll/Acknowledge APIs | Odoo (external, stub consumer) | Cloud Backend (.NET) | Provider-side only (OpenAPI validation) | OpenAPI spec |

**Version skew handling:** Pact contracts are versioned by Edge Agent APK version. CI runs provider verification against the latest 2 APK versions' contracts. A Cloud deploy that breaks a contract for any supported APK version fails the pipeline.

**Broker:** Pact Broker (Docker container in dev/CI, hosted service in staging/prod). Contract artifacts published on consumer CI build; provider CI verifies on every build.

### 5.8 FCC Session Takeover Testing (per vendor)

Tests verifying correct behavior when agent leadership transitions occur while the FCC hardware maintains active sessions. These scenarios are critical for multi-agent HA deployments where the FCC connection must be transferred without data loss.

| Scenario ID | Scenario | Vendor | Expected Result |
|---|---|---|---|
| TAKE-1 | New primary connects while old session is active | DOMS | TBD — pending P2-16 research: document whether DOMS accepts or rejects the new connection |
| TAKE-2 | New primary connects after old primary crash (no graceful close) | DOMS | TBD — pending P2-16 research: document TCP session timeout behavior and reconnection latency |
| TAKE-3 | Two agents connect simultaneously | DOMS | TBD — pending P2-16 research: document whether FCC accepts both, rejects second, or drops first |
| TAKE-4 | Primary sends pre-auth, crashes, new primary connects | DOMS | TBD — pending P2-16 research: document whether pre-auth remains active on FCC after session loss |
| TAKE-5 | New primary connects while old session is active | Petronite | TBD — to be tested when Petronite adapter supports multi-agent |
| TAKE-6 | New primary connects after old primary crash | Petronite | TBD — to be tested when Petronite adapter supports multi-agent |
| TAKE-7 | Two agents connect simultaneously | Petronite | TBD — to be tested when Petronite adapter supports multi-agent |
| TAKE-8 | Primary sends pre-auth, crashes, new primary connects | Petronite | TBD — to be tested when Petronite adapter supports multi-agent |

**Execution:** DOMS scenarios are executable in the virtual lab once P2-16 research is complete. Results from P2-16 will inform the "Expected Result" column. Repeat the scenario matrix for each vendor as their adapter is onboarded to multi-agent HA.

**Dependencies:** P2-16 (FCC vendor takeover research) provides the empirical data to fill expected results. Virtual lab must support multi-agent connection scenarios.

## 6. Validation and Edge Cases
- Flaky offline tests: use deterministic Toxiproxy control (API calls, not timing); set generous timeouts for CI
- FCC Simulator must not become a maintenance burden: keep it minimal (DOMS only for MVP); add vendors only when their adapter is built
- Edge Agent replay test on physical hardware requires dedicated CI step or manual execution during hardening phase
- Contract test consumer stubs for Odoo: since Odoo is external, use provider-side OpenAPI validation only (no Pact consumer for Odoo)

## 7. Cross-Component Impact
- **Cloud Backend:** Must include Testcontainers, PactNet, and k6 in CI pipeline (affects TODO 3.3)
- **Edge Agent:** Must include FCC Simulator dependency, Pact JVM, Robolectric in CI pipeline (affects TODO 3.3)
- **Angular Portal:** Jasmine/Karma for unit tests, Playwright for e2e (already in HLD); no contract testing needed (portal consumes Cloud API directly via OpenAPI types)

## 8. Dependencies
- **Prerequisites:** TODO 1.3 (API contracts — needed for contract tests and load test scripts), TODO 1.5 (FCC adapter interface — needed for simulator endpoint design), TODO 3.3 (CI/CD pipeline — tests must integrate into pipelines)
- **Downstream:** TODO 5.2 Phase 0 (FCC Simulator delivery), TODO 5.8 Phase 6 (load testing and offline stress testing execution)
- **Recommended next step:** Implement FCC Simulator (`tools/fcc-simulator/`) as the first testable deliverable

## 9. Open Questions
None. All decisions are closable from existing context.

## 10. Acceptance Checklist
- [ ] Unit test frameworks configured in all three component scaffolds with coverage gates
- [ ] Integration test projects created with Testcontainers (Cloud) and Room in-memory (Edge)
- [ ] FCC Simulator runs standalone and in-process, supports static/generator/error modes
- [ ] Docker Compose file for offline scenario testing with Toxiproxy exists and OFF-1 through OFF-6 are executable
- [ ] Cross-component E2E harness runs scenario 1 (ingest → poll → acknowledge) end-to-end
- [ ] k6 load test scripts exist for ingestion, poll, and batch upload targets
- [ ] Pact consumer tests exist in Edge Agent for upload, pre-auth, config, telemetry, registration APIs
- [ ] Pact provider verification runs in Cloud Backend CI against latest 2 APK version contracts
- [ ] Edge Agent 30,000-transaction replay test passes on Urovo i9100 hardware

## 11. Output Files to Create
- `docs/specs/testing/testing-strategy.md` (this file)

## 12. Recommended Next TODO
**3.5 Observability & Monitoring Design** — completes the Tier 3 engineering practices alongside testing and CI/CD.
