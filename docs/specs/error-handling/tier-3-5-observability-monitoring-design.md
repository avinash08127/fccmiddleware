# Observability and Monitoring Design

## 1. Output Location
- Target file path: `docs/specs/error-handling/tier-3-5-observability-monitoring-design.md`
- Optional companion files: None
- Why this location matches `docs/STRUCTURE.md`: the mapping guide places alerting in `/docs/specs/error-handling`; this TODO is centered on alert rules, monitoring thresholds, and operational runbooks, with logging and dashboards defined only to the level needed to implement those alerts.

## 2. Scope
- TODO item addressed: `3.5 Observability & Monitoring Design`
- In scope: cloud structured logging, correlation propagation, metrics, dashboards, edge telemetry monitoring fields, edge local log rotation, diagnostics screen content, alert rules, notification channels, and per-alert runbook structure
- Out of scope: application code scaffolding, vendor-specific dashboard screenshots, frontend analytics beyond correlation ID display, and SIEM export beyond the primary log destination

## 3. Source Traceability
- Requirements referenced: `REQ-14`, `REQ-15.12`, `REQ-16`, `NFR-7`
- HLD sections referenced only: Cloud Backend `8.7 Observability`; Edge Agent `8.8 Observability`; Angular Portal `3.1.4 Edge Agent Monitoring`, `8.6 Observability`
- Assumptions from TODO ordering/dependencies: `tier-1-1-telemetry-payload-spec.md` remains the canonical telemetry field contract; `tier-2-1-error-handling-strategy.md` remains the canonical error taxonomy and baseline alert semantics; `tier-2-4-edge-agent-configuration-schema.md` owns cloud-managed runtime config keys

## 4. Key Decisions

| Decision | Why | Impact |
|---|---|---|
| CloudWatch is the primary destination for logs, metrics, dashboards, and alarms. | HLD prefers AWS managed services for lower support burden. | MVP avoids operating ELK/Loki as a required dependency. |
| Correlation uses one canonical `correlationId` plus OpenTelemetry `traceId` and `spanId`. | Ops needs one searchable ID across portal, cloud, queues, and edge telemetry. | Every inbound request, async job, and alert payload must carry `correlationId`. |
| Alert thresholds are fixed in this spec with cloud-configurable values where device/site tuning is expected. | The TODO requires build-ready rules, not placeholders. | Portal settings and config APIs must expose the listed thresholds and channels. |
| Edge telemetry remains best-effort, but offline detection is based on absence of telemetry plus last successful sync. | HLD already states telemetry is not buffered. | Alerting must tolerate missed individual reports without paging on the first gap. |

## 5. Detailed Specification

### 5.1 Cloud Logging and Correlation Contract

| Item | Rule |
|---|---|
| Log format | JSON, one event per line, UTC timestamps only |
| Required fields on every cloud log | `timestampUtc`, `level`, `service`, `environment`, `correlationId`, `traceId`, `spanId`, `message`, `eventType` |
| Conditional business context fields | `siteCode`, `legalEntityId`, `deviceId`, `transactionId`, `bufferRecordId`, `queueName`, `workerName`, `httpMethod`, `httpPath`, `httpStatus`, `errorCode`, `exceptionType`, `fccVendor`, `ingestionMode`, `configVersion`, `agentVersion` |
| Correlation ID generation | Accept inbound `X-Correlation-Id`; if absent, generate UUIDv7 at the first public ingress point |
| Correlation propagation | Portal request header -> backend API -> internal command handlers -> SQS message attributes -> worker logs -> outbound calls -> alert payloads |
| Edge propagation | Edge sends `X-Correlation-Id` on every cloud API call; cloud echoes the value in response headers so the portal and diagnostics screens can display it |
| Log levels | `INFO` for lifecycle/business milestones, `WARN` for retryable degradation, `ERROR` for failed operations requiring operator or engineer attention, `DEBUG`/`TRACE` disabled in production by default |
| Sensitive data policy | Never log device tokens, FCC credentials, raw PAN-equivalent payment data, customer tax ID, or full Odoo payload bodies |
| Destination | ECS/app logs -> CloudWatch Logs log groups per service and environment; retention `30 days` hot, export to S3 for `365 days` |

### 5.2 Cloud Metrics and Dashboards

| Metric Name | Type | Dimensions | Source | Alarmed | Dashboard Usage |
|---|---|---|---|---|---|
| `api_request_rate_per_min` | Counter | `service`, `route`, `environment` | API middleware | No | Backend throughput |
| `api_request_latency_ms_p50/p95/p99` | Histogram | `service`, `route`, `environment` | API middleware | Yes on `p95` | Latency panel |
| `api_error_rate_percent` | Gauge | `service`, `route`, `environment` | API + worker aggregation | Yes | Error overview |
| `ingestion_events_per_min` | Counter | `siteCode`, `ingestionMode` | Ingestion pipeline | No | Ingestion throughput |
| `ingestion_error_rate_percent` | Gauge | `siteCode`, `errorCode` | Ingestion pipeline | Yes | Ingestion health |
| `odoo_poll_latency_ms_p95` | Histogram | `legalEntityId`, `environment` | Odoo poll worker | Yes | Odoo sync dashboard |
| `reconciliation_match_rate_percent` | Gauge | `legalEntityId`, `environment` | Reconciliation worker | No | Reconciliation dashboard |
| `sqs_queue_depth` | Gauge | `queueName`, `environment` | CloudWatch/SQS | Yes for selected queues | Buffer and retry depth |
| `db_connection_pool_in_use_percent` | Gauge | `dbCluster`, `service` | App instrumentation | Yes | DB saturation |
| `edge_buffer_depth_records` | Gauge | `siteCode`, `deviceId` | Telemetry ingest | Yes | Site health |
| `edge_sync_lag_hours` | Gauge | `siteCode`, `deviceId` | Telemetry ingest | Yes | Site health |
| `fcc_heartbeat_age_minutes` | Gauge | `siteCode`, `deviceId` | Telemetry ingest | Yes | Site health |
| `master_data_staleness_hours` | Gauge | `legalEntityId`, `dataset` | Master data sync job | Yes | Data freshness |

Dashboard set:

| Dashboard | Required Widgets |
|---|---|
| `ops-overview` | `api_request_rate_per_min`, `api_error_rate_percent`, `api_request_latency_ms_p95`, `sqs_queue_depth`, active alerts |
| `ingestion-pipeline` | `ingestion_events_per_min`, `ingestion_error_rate_percent`, `sqs_queue_depth` for ingest/retry/DLQ, top `errorCode` table |
| `odoo-sync` | `odoo_poll_latency_ms_p95`, `edge_sync_lag_hours`, stale pending count, worker failure count |
| `reconciliation` | `reconciliation_match_rate_percent`, manual-review queue depth, exception rate |
| `edge-fleet-health` | `fcc_heartbeat_age_minutes`, `edge_buffer_depth_records`, `edge_sync_lag_hours`, offline agent count, version distribution |

### 5.3 Edge Agent Monitoring and Local Logging

Telemetry fields required for monitoring are sourced from [tier-1-1-telemetry-payload-spec.md](/mnt/c/Users/a0812/fccmiddleware/docs/specs/data-models/tier-1-1-telemetry-payload-spec.md). The monitoring implementation must surface at least these fields in cloud dashboards and the portal agent detail page:

| Field | Source Path | Notes |
|---|---|---|
| Battery percent | `device.batteryPercent` | Show as red below `20` |
| Storage free MB | `device.storageFreeMb` | Show as red below `1024` |
| Buffer depth | `buffer.totalRecords` | Primary operational backlog indicator |
| FCC heartbeat age seconds | `fccHealth.heartbeatAgeSeconds` | Basis for stale-heartbeat alert |
| Last sync timestamp | `sync.lastSuccessfulSyncUtc` | Show exact UTC and local site time |
| Sync lag seconds | `sync.syncLagSeconds` | Basis for sync lag alert |
| App version | `device.appVersion` | Compared against minimum supported version |
| Error counts | `errorCounts.*` | Show top three non-zero categories |

Edge local log rules:

| Config Key | Default | Validation | Apply Mode | Rule |
|---|---|---|---|---|
| `telemetry.logLevel` | `INFO` | `TRACE|DEBUG|INFO|WARN|ERROR` | Hot-reload | Existing config key; governs local file and Logcat verbosity |
| `telemetry.logRotationMaxFileSizeMb` | `10` | `1-50` | Hot-reload | Rotate active file when size threshold reached |
| `telemetry.logRotationMaxFiles` | `5` | `2-20` | Hot-reload | Keep most recent rotated files only |

Local file behavior: write structured line logs to app-private storage, rotate by size, delete oldest file first, and keep the last `100` entries indexed for the diagnostics screen regardless of file boundaries.

Diagnostics screen wireframe content:

| Section | Must Show | Actions |
|---|---|---|
| Connectivity | FCC status, internet status, cloud reachability, last heartbeat age | `Run connectivity test` |
| Buffer | total records, oldest pending age, failed record count, last upload result | `Retry sync now` |
| Sync | last sync attempt, last successful sync, sync lag, config version, app version | `Pull config now` |
| Device | battery percent, charging state, storage free, device model, OS version | None |
| Recent logs | last 100 entries with `timestamp`, `level`, `eventType`, `message`, `correlationId` | Filter by level |

### 5.4 Alert Rules, Severity, and Channels

| Alert Key | Condition | Default Threshold | Evaluation Window | Severity | Channels |
|---|---|---|---|---|---|
| `fcc-heartbeat-stale` | `fcc_heartbeat_age_minutes > threshold` | `5 minutes` | `2 consecutive telemetry intervals` | High | Portal notification, email |
| `transaction-buffer-depth-high` | `edge_buffer_depth_records > threshold` | `5000 records` | `15 minutes` | High | Portal notification, email |
| `cloud-sync-lag-high` | `edge_sync_lag_hours > threshold` | `2 hours` | `15 minutes` | High | Portal notification, email, SMS after `6 hours` |
| `ingestion-error-rate-spike` | `ingestion_error_rate_percent > threshold` | `5%` | `5 minutes` | Critical | Portal notification, email, PagerDuty |
| `edge-agent-offline` | no telemetry received and `lastSuccessfulSyncUtc` older than threshold | `2 hours` | `1 evaluation` | Critical | Portal notification, email, SMS, PagerDuty |
| `master-data-stale` | `master_data_staleness_hours > threshold` | `24 hours` | `30 minutes` | High | Portal notification, email |

Alert routing rules:

| Channel | Use |
|---|---|
| Portal notification | All active alerts; visible in ops dashboard and site detail |
| Email | High and Critical alerts to country ops distribution list |
| SMS | Escalation for prolonged site-impacting alerts (`cloud-sync-lag-high`, `edge-agent-offline`) |
| PagerDuty | Critical platform alerts and site-wide agent outages |

Deduplication and recovery:
- Open one active alert per `alertKey + siteCode + deviceId` where applicable.
- Auto-resolve after `3` consecutive healthy evaluations.
- Re-notify every `4 hours` while the alert remains open.
- Suppress `transaction-buffer-depth-high` while `edge-agent-offline` is already open for the same device.

### 5.5 Runbook Structure

One runbook document is required per alert key, each following this fixed structure:

| Section | Required Content |
|---|---|
| `Purpose` | What the alert means and business impact |
| `Trigger` | Exact metric, threshold, and evaluation window from this spec |
| `Immediate checks` | Dashboard panels, log queries, and API/portal screens to inspect first |
| `Common causes` | Top expected failure modes |
| `Mitigation` | Ordered operator actions with stop conditions |
| `Escalation` | When to page engineering, vendor, or country ops |
| `Recovery verification` | Exact signals showing the alert is resolved |
| `Post-incident capture` | Required audit notes, ticket link, and correlation IDs |

## 6. Validation and Edge Cases
- Missing one telemetry report must not open `edge-agent-offline`; the threshold is measured from the last successful sync, not a single skipped post.
- `ingestion_error_rate_percent` excludes dedup conflicts and explicit client validation rejections from the numerator.
- If device clock skew makes `lastSuccessfulSyncUtc` appear in the future, clamp displayed sync lag to `0` and raise a `WARN` log; do not open `cloud-sync-lag-high`.
- Cloud-generated `correlationId` takes precedence when an inbound header is malformed or longer than `128` characters.

## 7. Cross-Component Impact
- Cloud Backend: implement log enrichment, CloudWatch metric export, dashboards, alarms, alert deduplication, and correlation propagation through SQS/workers.
- Edge Agent: emit telemetry fields already defined in the telemetry contract, apply local rotation settings, include `correlationId` on cloud calls, and expose the diagnostics screen sections listed above.
- Angular Portal: show active alerts, agent health widgets, telemetry drill-down, and surfaced `correlationId` values for support workflows.

## 8. Dependencies
- Prerequisites: `docs/specs/data-models/tier-1-1-telemetry-payload-spec.md`, `docs/specs/error-handling/tier-2-1-error-handling-strategy.md`, `docs/specs/config/tier-2-4-edge-agent-configuration-schema.md`
- Downstream TODOs affected: monitoring and alerting setup, production deployment runbook, coding conventions logging guidance
- Recommended next implementation step: write the six alert-specific runbooks and create the CloudWatch dashboards/alarms as infrastructure definitions

## 9. Open Questions
- Question: The local diagnostics screen authentication model is still unresolved in the Edge HLD.
- Recommendation: keep the screen supervisor-only and align the auth mechanism in the security TODO before implementation starts.
- Risk if deferred: monitoring data is defined, but field access control for on-device diagnostics may be implemented inconsistently.

## 10. Acceptance Checklist
- [ ] Cloud JSON log format and required fields are fixed.
- [ ] Correlation ID generation and propagation rules are fixed across portal, cloud, queues, and edge.
- [ ] Primary log destination is selected and retention is specified.
- [ ] Required cloud metrics and dashboard widgets are fixed.
- [ ] Edge monitoring fields are mapped to the telemetry contract without redefining the payload.
- [ ] Edge log rotation keys, defaults, and limits are fixed.
- [ ] Diagnostics screen sections and actions are fixed.
- [ ] All six alert rules have thresholds, windows, severities, and channels.
- [ ] Alert deduplication, re-notification, and auto-resolution rules are fixed.
- [ ] Runbook template is fixed for one runbook per alert type.

## 11. Output Files to Create
- `docs/specs/error-handling/tier-3-5-observability-monitoring-design.md`

## 12. Recommended Next TODO
`Monitoring and alerting setup (all rules active, runbooks written)`
