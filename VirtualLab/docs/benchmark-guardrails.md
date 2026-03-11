# Virtual Lab Benchmark Guardrails

This document is the Phase 0 source of truth for latency budgets, verification cases, and the benchmark seed profile referenced by later virtual lab tasks.

## Benchmark seed profile

Use [`config/benchmark-seed.json`](/mnt/c/Users/a0812/fccmiddleware/VirtualLab/config/benchmark-seed.json) unless a later task explicitly calls for a larger scenario.

| Dimension | Value |
|-----------|-------|
| Sites | 10 |
| Pumps per site | 10 |
| Nozzles per pump | 4 |
| Total nozzles | 400 |
| Transactions | 10,000 |
| Delivery mix | 4,000 push, 3,500 pull, 2,500 hybrid |
| Scenario mix | 7,000 normal orders, 1,500 create-only pre-auth, 1,500 create-then-authorize pre-auth |
| Callback targets | 10 |
| Deterministic seed | `424242` |

## Pass/fail thresholds

| Guardrail | Dataset | Pass threshold | Verification method |
|-----------|---------|----------------|---------------------|
| Seeded lab startup readiness | Default seed profile | Usable in `<= 5 minutes` from first local startup command to dashboard ready state | Local smoke checklist and startup stopwatch |
| Dashboard/site load latency | Default seed profile | `p95 <= 2.0 s` | UI/API latency probe plus browser network timing |
| SignalR live update latency | Default seed profile | `p95 <= 500 ms` from action accepted to UI state reflected | Live action smoke run using correlation timestamp |
| FCC emulator success-path latency | Default seed profile | `p95 <= 300 ms` excluding intentional simulation delay | Emulator probe via diagnostics endpoint and smoke calls |
| Transaction pull first page latency | `limit <= 100` on 10,000 transactions | `p95 <= 250 ms` | Diagnostics query timing and direct endpoint call |
| Deterministic replay | Same seed and active profile config | Identical transaction ordering, correlation lineage, and scenario result counts across repeated runs | Replay signature comparison across `3` runs |
| Callback retry deduplication | Retry-enabled callback attempts | No duplicate transaction row and no duplicate success log for one logical attempt | Integration test and manual retry smoke check |
| Payload observability completeness | All simulated transaction and pre-auth flows | Raw payload, canonical payload when produced, correlation ID, and event history always present | Spot-check queries and automated persistence assertions |

## Explicit verification cases

### 1. Seeded lab startup readiness

- Start the API and apply the default seed profile.
- Load the dashboard with the seeded environment.
- Record elapsed time from first startup command to dashboard data visible.
- Fail if the workflow exceeds `5 minutes` or requires manual repair steps not in the README.

### 2. Dashboard and site load latency

- Run the backend latency probe against the default seed profile.
- Load `/api/dashboard` and `/api/sites` repeatedly under local dev conditions.
- Fail if the computed `p95` exceeds `2 seconds`.

### 3. SignalR live update latency

- Trigger a representative live action such as nozzle lift or dispense.
- Compare the server-side event timestamp to the timestamp rendered in the UI update.
- Fail if `p95` exceeds `500 ms`.

### 4. FCC emulator endpoint latency

- Probe `/fcc/{siteCode}/health` and `/fcc/{siteCode}/transactions`.
- Exclude any configured artificial delay from the measurement.
- Fail if the measured success-path `p95` exceeds `300 ms`.

### 5. Transaction pull query latency

- Execute a first-page transaction listing using `limit <= 100`.
- Verify sort/filter work on the default 10,000-transaction dataset.
- Fail if query or end-to-end API `p95` exceeds `250 ms`.

### 6. Deterministic replay

- Run the same scenario seed three times with identical profile configuration.
- Compare replay signature, transaction count, delivery mode count, and ordered scenario steps.
- Fail on any drift.

## Deterministic replay expectations

Replay determinism for the virtual lab means:

- the same scenario seed and profile configuration generate the same ordered transaction IDs and correlation IDs;
- delivery mode counts and pre-auth mode counts remain identical across runs;
- callback retry decisions and success/failure tallies remain stable unless the input profile changes;
- timestamps may differ, but relative ordering and replay signature must not.

The Phase 0 replay signature is the SHA-256 hash of the seed profile dimensions and scenario seed. Later phases should extend the signature with persisted scenario definition content once scenario storage exists.

## Phase references

Later tasks should treat this document as the baseline artifact for performance-sensitive work:

- `VL-0.2` uses the same seed profile for persistence hot-path indexes.
- `VL-0.3` seeds the documented profile dimensions by default.
- `VL-1.4` and `VL-2.4` should reuse the transaction pull and callback verification cases.
- `VL-2.3` should reuse the SignalR latency target and verification method.
- `VL-3.4` should reuse the deterministic replay definition and signature rules.
