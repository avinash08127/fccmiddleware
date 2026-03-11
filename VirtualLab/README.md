# Virtual Lab

Phase 0 scaffolding plus the initial persistence baseline for the FCC simulator and pump simulator virtual lab lives here.

## Workspace layout

- `src/` contains the .NET backend solution, EF Core persistence model, SQLite migration, and benchmark diagnostics endpoint.
- `ui/virtual-lab/` contains the Angular standalone frontend scaffold.
- `config/benchmark-seed.json` is the reusable baseline seed profile for performance and smoke checks.
- `docs/benchmark-guardrails.md` defines the measurable guardrails and pass/fail criteria.
- `docs/smoke-validation-checklist.md` defines the local and Azure-hosted validation checklist.
- `scripts/run-benchmarks.mjs` runs the repeatable Phase 0 latency probe against a running backend.

## Local development

1. Start the API from `VirtualLab/src` with `dotnet run --project VirtualLab.Api`.
2. Start the UI from `VirtualLab/ui/virtual-lab` with `npm install` then `npm start`.
3. Run the benchmark harness from `VirtualLab` with `node scripts/run-benchmarks.mjs`.

The Angular dev server proxies `/api`, `/fcc`, `/callbacks`, and `/hubs` to the API.

## Seeded demo environment

On local and development startup the API applies migrations and seeds a deterministic default environment into SQLite.

- Default site: `VL-MW-BT001` (`Blantyre Demo Site`)
- Default profile path: `doms-like` profile on `VL-MW-BT001`
- Additional seeded profiles:
  - `generic-create-only`
  - `generic-create-then-authorize`
  - `bulk-push`
- Auth mode coverage:
  - `NONE`
  - `API_KEY`
  - `BASIC_AUTH`
- Pre-auth mode coverage:
  - `CREATE_ONLY`
  - `CREATE_THEN_AUTHORIZE`

## Seed and reset

- Automatic startup seeding is controlled by `VirtualLab:Seed:ApplyOnStartup`.
- To force a fresh reset and reseed, set `VirtualLab:Seed:ResetOnStartup=true` before running the API.
- To trigger seeding manually after startup, call `POST /api/admin/seed`.
- To reset and reseed manually, call `POST /api/admin/seed?reset=true`.
