# Edge Agent — Performance Budgets

**Task:** EA-0.0
**Status:** Baseline established — referenced by all later Edge Agent tasks

---

## 1. Latency Guardrails

| Endpoint / Operation | Metric | Threshold | Notes |
|----------------------|--------|-----------|-------|
| `POST /api/preauth` | p95 local API overhead | **≤ 150 ms** | Before FCC call time; on-device only |
| `POST /api/preauth` | p95 end-to-end on healthy FCC LAN | **≤ 1.5 s** | Includes FCC round-trip |
| `POST /api/preauth` | p99 end-to-end on healthy FCC LAN | **≤ 3 s** | |
| `GET /api/transactions` | p95 first page (`limit ≤ 50`) with 30,000 buffered records | **≤ 150 ms** | Buffer-backed; no live FCC access |
| `GET /api/status` | p95 | **≤ 100 ms** | Aggregated local snapshot |
| `GET /api/pump-status` | Live response on healthy LAN | **≤ 1 s** | With short FCC timeout |
| `GET /api/pump-status` | Stale fallback (FCC unreachable) | **≤ 150 ms** | Last-known metadata |

## 2. Throughput Guardrails

| Operation | Threshold | Notes |
|-----------|-----------|-------|
| Cloud replay upload | **≥ 600 transactions/minute** | Chronological ordering must be preserved |

## 3. Memory Guardrail

| Metric | Threshold | Notes |
|--------|-----------|-------|
| Steady-state RSS during normal operation | **≤ 180 MB** | During polling + sync on Urovo i9100 |

## 4. Battery Guardrails

| Operating Mode | 8-hour shift drain attributable to Edge Agent | Notes |
|----------------|-----------------------------------------------|-------|
| `CLOUD_DIRECT` | **≤ 8%** | Low-activity safety-net poller |
| `RELAY` | **≤ 12%** | Primary receiver with active polling |
| `BUFFER_ALWAYS` | **≤ 12%** | Always-buffer with periodic upload |

---

## 5. Benchmark Test Paths

The following automated paths must be measurable before Phase 2 implementation begins.

| Path | Test File | Dataset | Pass Condition |
|------|-----------|---------|----------------|
| Room query latency — transaction page | `RoomQueryBenchmarkTest` | 30,000 buffered records | p95 `findPage(50, 0)` ≤ 150 ms |
| Room query latency — pending batch | `RoomQueryBenchmarkTest` | 30,000 buffered records | p95 `findByStatus("PENDING", 50)` ≤ 150 ms |
| Local API — GET /api/transactions | `LocalApiBenchmarkTest` | 30,000 buffered records | p95 ≤ 150 ms |
| Local API — GET /api/status | `LocalApiBenchmarkTest` | Running service | p95 ≤ 100 ms |
| Cloud replay throughput | `ReplayThroughputBenchmarkTest` | 1,000-record batch against mock | ≥ 600 tx/min |
| Pre-auth local overhead | `PreAuthBenchmarkTest` | No FCC call; local-only path | p95 ≤ 150 ms |

---

## 6. Synthetic Dataset Specification

Benchmarks use a representative 30,000-record in-memory Room dataset seeded by `SeedDataGenerator`.

| Field | Value Strategy |
|-------|---------------|
| `id` | UUID v4 |
| `fccTransactionId` | Sequential `FCC-{n:08d}` |
| `siteCode` | Alternating across 3 sites: `SITE_001`, `SITE_002`, `SITE_003` |
| `syncStatus` | Weighted distribution: 70% `PENDING`, 20% `UPLOADED`, 10% `ARCHIVED` |
| `rawPayload` | Minimal JSON string (~200 bytes) |
| `normalizedPayload` | Minimal JSON string (~300 bytes) |
| `createdAt` | Timestamps spread over 30-day window ending now (ISO 8601 UTC) |
| `uploadedAt` | Non-null only for `UPLOADED`/`ARCHIVED` records |
| `retryCount` | 0 for most; 1–3 for ~5% of records |

---

## 7. Urovo i9100 Profiling Checklist

Use this checklist for on-device validation runs before each release.

### Setup
- [ ] Device is Urovo i9100 running Android 12 (API 31+)
- [ ] Edge Agent installed in `release` build variant (minified)
- [ ] ADB connected; Android Studio profiler or `adb shell dumpsys meminfo` available
- [ ] 30,000 synthetic records pre-seeded in the local Room database

### Latency Checks
- [ ] Run `POST /api/preauth` × 100 iterations; verify p95 local overhead ≤ 150 ms
- [ ] Run `GET /api/transactions?limit=50` × 50 iterations; verify p95 ≤ 150 ms
- [ ] Run `GET /api/status` × 50 iterations; verify p95 ≤ 100 ms
- [ ] Simulate FCC LAN unreachable; verify `GET /api/pump-status` stale fallback ≤ 150 ms

### Throughput Check
- [ ] Trigger replay with mock cloud accepting 200 OK; measure transactions per minute
- [ ] Verify ≥ 600 tx/min sustained for 60 seconds
- [ ] Verify upload order is strictly chronological (`created_at ASC`)

### Memory Check
- [ ] Capture RSS after 30 min steady-state operation (polling + sync active)
- [ ] Verify RSS ≤ 180 MB via `adb shell dumpsys meminfo com.fccmiddleware.edge`

### Battery Check
- [ ] Charge to 100%; run 8-hour soak test in `RELAY` mode
- [ ] Verify Edge Agent battery attribution ≤ 12% via `adb shell dumpsys batterystats`

### Regression Gates
- [ ] All automated benchmark tests pass in CI before merging Phase 1–3 PRs
- [ ] On-device checklist completed before each QA release to Urovo devices

---

## 8. CI Integration

Benchmark tests run locally and in CI. The following gradle command executes the benchmark suite:

```bash
./gradlew :app:testDebugUnitTest --tests "com.fccmiddleware.edge.benchmark.*"
```

A test is considered **failing** if:
- Any measured p95 exceeds the threshold in §1 above
- Replay throughput falls below 600 tx/min in §2 above
- The 30,000-record seeded dataset cannot be inserted within 30 seconds
