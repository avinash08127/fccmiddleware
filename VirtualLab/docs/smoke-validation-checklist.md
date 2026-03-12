# Virtual Lab Smoke Validation Checklist

## Local smoke run

- [ ] `dotnet run --project VirtualLab/src/VirtualLab.Api` starts without configuration edits.
- [ ] Swagger and `/healthz` respond locally.
- [ ] Default benchmark seed profile from [`config/benchmark-seed.json`](/mnt/c/Users/a0812/fccmiddleware/VirtualLab/config/benchmark-seed.json) is loaded.
- [ ] `node scripts/run-benchmarks.mjs` reports a passing local probe.
- [ ] `npm start` serves the Angular app and proxy calls to the backend succeed.
- [ ] Dashboard route loads and renders seeded metrics.
- [ ] `/fcc/{siteCode}/health` and `/fcc/{siteCode}/transactions?limit=100` return successfully.
- [ ] SignalR hub `/hubs/live` negotiates and receives at least one broadcast event.
- [ ] Replay signature is stable across three runs with the same scenario seed.

## Azure-hosted smoke run

- [ ] API configuration points at the intended database provider and environment name.
- [ ] Azure Web App app settings include the intended `VirtualLab__Persistence__*` values and `VirtualLab__Cors__AllowedOrigins__*` entries.
- [ ] UI `assets/config/runtime-config.json` points at the Azure API origin and SignalR hub origin.
- [ ] Seed/reset path uses the default benchmark profile or an explicitly documented override.
- [ ] `/healthz` succeeds from the Azure-hosted deployment.
- [ ] Diagnostics latency probe runs against the hosted API and stays within the same guardrails unless a deployment note documents a justified variance.
- [ ] Dashboard, sites, transactions, and logs routes load from the hosted UI.
- [ ] SignalR live updates propagate through the hosted API path.
- [ ] FCC emulator endpoints enforce the configured profile auth mode.
- [ ] Callback capture history is retrievable and duplicate retries are not observed.

## Evidence to capture

- Benchmark harness console output
- API startup time
- Browser network timing for dashboard load
- Replay signature values for each repeated run
- Environment name and configuration source used during the smoke run
