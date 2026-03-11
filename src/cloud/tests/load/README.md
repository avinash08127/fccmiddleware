# Cloud Backend Load Tests

This directory contains the k6 harness for `CB-6.1: Load Testing`.

## Scenarios

- `sustained_ingestion`: `POST /api/v1/transactions/ingest` at 23 req/sec for 1 hour in the `acceptance` profile.
- `burst_ingestion`: `POST /api/v1/transactions/ingest` at 100 req/sec for 5 minutes.
- `odoo_poll_under_load`: 20 poll requests/sec, matching 10 concurrent pollers running at 2 req/sec each.
- `odoo_acknowledge`: polls for up to 100 pending records, then posts one 100-item acknowledge batch every 5 seconds.
- `edge_agent_upload`: 50 concurrent Edge Agent uploads, each sending 100 canonical transactions once.

## Required environment variables

- `K6_BASE_URL`: Cloud API base URL, for example `https://cloud-api.dev.example.com`
- `K6_LEGAL_ENTITY_ID`: tenant UUID used by the Odoo API key and device JWT
- `K6_SITE_CODE`: site code with a valid FCC config, for example `LOAD-SITE-001`
- `K6_ODOO_API_KEY`: raw Odoo API key for the same tenant
- `K6_FCC_API_KEY_ID`: FCC client ID sent in `X-Api-Key`
- `K6_FCC_HMAC_SECRET`: FCC shared secret used to compute `X-Signature`
- `K6_DEVICE_JWT_SIGNING_KEY`: HMAC signing key configured by `DeviceJwt:SigningKey`

## Optional environment variables

- `K6_PROFILE`: `smoke` or `acceptance`. Default: `acceptance`
- `K6_DEVICE_ID`: device subject for upload JWTs. Default: `load-device-01`
- `K6_DEVICE_JWT_ISSUER`: default `fcc-middleware-cloud`
- `K6_DEVICE_JWT_AUDIENCE`: default `fcc-middleware-api`
- `K6_FCC_VENDOR`: default `DOMS`
- `K6_PRODUCT_CODE`: default `PMS`
- `K6_CURRENCY_CODE`: default `GHS`
- `K6_PAGE_SIZE`: poll page size. Default: `100`
- `K6_INCLUDE_POLL_FROM`: set to `true` to add a rolling `from` filter to poll requests
- `K6_SUMMARY_EXPORT`: file path for a JSON summary export

## Run examples

Smoke run:

```bash
k6 run \
  -e K6_PROFILE=smoke \
  -e K6_BASE_URL=http://localhost:8080 \
  -e K6_LEGAL_ENTITY_ID=99000000-0000-0000-0000-000000000021 \
  -e K6_SITE_CODE=POLL-SITE-001 \
  -e K6_ODOO_API_KEY=test-odoo-api-key \
  -e K6_FCC_API_KEY_ID=fcc-client-001 \
  -e K6_FCC_HMAC_SECRET=fcc-secret-integration-key-32-chars \
  -e K6_DEVICE_JWT_SIGNING_KEY=TestSigningKey-Portal-Integration-256bits!!!!! \
  src/cloud/tests/load/cloud-backend-load.js
```

Acceptance run:

```bash
k6 run \
  -e K6_PROFILE=acceptance \
  -e K6_BASE_URL=https://cloud-api.staging.example.com \
  -e K6_LEGAL_ENTITY_ID=<tenant-guid> \
  -e K6_SITE_CODE=<load-site-code> \
  -e K6_ODOO_API_KEY=<raw-odoo-api-key> \
  -e K6_FCC_API_KEY_ID=<fcc-client-id> \
  -e K6_FCC_HMAC_SECRET=<fcc-hmac-secret> \
  -e K6_DEVICE_JWT_SIGNING_KEY=<device-jwt-signing-key> \
  -e K6_SUMMARY_EXPORT=src/cloud/tests/load/results/acceptance-summary.json \
  src/cloud/tests/load/cloud-backend-load.js
```

Docker-based run if `k6` is not installed locally:

```bash
docker run --rm -i \
  -v "$PWD:/work" \
  -w /work \
  -e K6_PROFILE=smoke \
  -e K6_BASE_URL=http://host.docker.internal:8080 \
  -e K6_LEGAL_ENTITY_ID=99000000-0000-0000-0000-000000000021 \
  -e K6_SITE_CODE=POLL-SITE-001 \
  -e K6_ODOO_API_KEY=test-odoo-api-key \
  -e K6_FCC_API_KEY_ID=fcc-client-001 \
  -e K6_FCC_HMAC_SECRET=fcc-secret-integration-key-32-chars \
  -e K6_DEVICE_JWT_SIGNING_KEY=TestSigningKey-Portal-Integration-256bits!!!!! \
  grafana/k6 run src/cloud/tests/load/cloud-backend-load.js
```

## Validation targets

- Sustained and burst ingestion: `p99 < 500ms`, `http_req_failed == 0`
- Odoo poll: `p95 < 200ms`, `http_req_failed == 0`
- Edge upload: `p95 < 2000ms`, `http_req_failed == 0`
- Acknowledge: `http_req_failed == 0`

## DB connection pool measurement

Run the SQL in [connection-pool-usage.sql](/mnt/c/Users/a0812/fccmiddleware/src/cloud/tests/load/db/connection-pool-usage.sql) during the test window:

```bash
psql "$FCCMIDDLEWARE_CONNECTION_STRING" \
  -f src/cloud/tests/load/db/connection-pool-usage.sql
```

Pass criterion from `CB-6.1`: `utilization_pct < 80`.

## Notes

- The ingest scenarios sign each request with the same HMAC scheme enforced by `POST /api/v1/transactions/ingest`.
- The upload scenario signs a JWT inline using the configured HMAC key, which matches the current development auth implementation.
- The acknowledge scenario is intentionally self-feeding: it polls first, then acknowledges the returned transaction IDs so it can run against a shared staging environment without extra fixture plumbing.
