# Desktop Edge Agent — Performance Guardrails

> **DEA-0.0** — Defined before feature development. All phase tasks must reference and satisfy these thresholds.

---

## Guardrail Table

| Metric | Target | Measurement Method | Benchmark Category |
|--------|--------|-------------------|--------------------|
| `POST /api/preauth` local overhead | p95 ≤ 50 ms (excl. FCC call time) | WebApplicationFactory integration test | `PreAuth` |
| `POST /api/preauth` end-to-end (healthy FCC LAN) | p95 ≤ 1.5 s; p99 ≤ 3 s | Integration test with FCC stub | `PreAuth` |
| `GET /api/transactions` first page (limit=50, 30k records) | p95 ≤ 100 ms | `TransactionQueryBenchmarks.FirstPage_Limit50` | `TransactionQuery` |
| `GET /api/status` | p95 ≤ 50 ms | WebApplicationFactory integration test | `Status` |
| `GET /api/pump-status` (live, healthy LAN) | ≤ 1 s | Integration test with FCC stub | `PumpStatus` |
| `GET /api/pump-status` (stale fallback) | ≤ 50 ms | `PumpStatusController` unit test | `PumpStatus` |
| Steady-state RSS | ≤ 250 MB | Process RSS monitor during 1-hour soak test | `Memory` |
| Replay throughput (stable internet) | ≥ 600 tx/min (chronological order preserved) | `ReplayThroughputBenchmarks.SerializeBatch50` + upload integration | `ReplayThroughput` |
| Cold start to API-ready | ≤ 5 s | Service startup integration test (wall clock) | `Startup` |
| Installer size (self-contained single-file) | ≤ 60 MB | CI publish artifact size check | `Build` |

---

## Running Benchmarks

```bash
# Run all benchmarks (Release mode required)
dotnet run -c Release --project tests/FccDesktopAgent.Benchmarks

# Run a specific category
dotnet run -c Release --project tests/FccDesktopAgent.Benchmarks -- --filter "*TransactionQuery*"
dotnet run -c Release --project tests/FccDesktopAgent.Benchmarks -- --filter "*ReplayThroughput*"
dotnet run -c Release --project tests/FccDesktopAgent.Benchmarks -- --filter "*Memory*"
```

---

## Benchmark Categories and Files

| Category | File | What It Measures |
|----------|------|-----------------|
| `TransactionQuery` | `TransactionQueryBenchmarks.cs` | EF Core + SQLite query latency (first page, cursor page, replay scan) against 30,000 records |
| `ReplayThroughput` | `ReplayThroughputBenchmarks.cs` | Serialization throughput, ordering cost, batch marking overhead |
| `Memory` | `MemoryFootprintBenchmarks.cs` | Allocation pressure for buffer materialization and upload batch serialization |
| `PreAuth` | (DEA-2.x) | `IPreAuthHandler` overhead — implemented in DEA-2.x alongside the handler |
| `Status` | (DEA-2.x) | `GET /api/status` latency — added alongside the status controller implementation |

---

## Synthetic Dataset

`tests/FccDesktopAgent.Benchmarks/Seed/TransactionSeeder.cs` generates 30,000 representative transactions:

- **Seed**: Fixed (`42`) for reproducible runs
- **Time range**: Last 7 days (covers worst-case 1-week offline backlog at ~60 tx/hr)
- **Pumps**: 6 pumps (`P01`–`P06`)
- **Fuel grades**: Unleaded 91, Unleaded 95, Diesel, Premium 98
- **Amount range**: 500 to 15,000 minor units (5.00 to 150.00 in currency with 2 decimal places)
- **Volume range**: 5L to 80L
- **Sync states**: 10% `Uploaded`, 90% `Pending` (realistic replay backlog)

---

## Guardrail Enforcement in CI

The guardrail thresholds documented here are enforced in CI via:

1. **Unit test assertions** (fast, always-on): Tests in `FccDesktopAgent.Core.Tests` and `FccDesktopAgent.Api.Tests` assert correctness of pagination, ordering, and state machine logic.
2. **BenchmarkDotNet baselines** (on-demand, Release): Run `FccDesktopAgent.Benchmarks` in CI nightly or before merging DEA-2.x+ tasks to catch regressions.
3. **Integration test timing assertions** (DEA-2.x+): Integration tests use `Stopwatch` to assert wall-clock thresholds on API endpoints against a seeded SQLite database.

---

## Architecture Rules Backing These Guardrails

| Rule | Guardrail |
|------|-----------|
| Rule #11 — Pre-auth is the top latency path; cloud is async | `POST /api/preauth` p95 local overhead ≤ 50 ms |
| Rule #12 — Offline reads are buffer-backed, never live FCC | `GET /api/transactions` p95 ≤ 100 ms against 30k records |
| Rule #13 — Pump status: single-flight + stale fallback | `GET /api/pump-status` stale fallback ≤ 50 ms |
| Rule #2 — No transaction left behind (chronological replay) | Replay throughput ≥ 600 tx/min, CreatedAt ASC ordering preserved |
| Rule #3 — SQLite WAL mode always enabled | Benchmarks configure `PRAGMA journal_mode=WAL` |
