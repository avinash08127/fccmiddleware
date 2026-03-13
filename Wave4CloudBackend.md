# Wave 4 Cloud Backend Review

**Date:** 2026-03-13
**Scope:** `src/cloud` API, application, infrastructure, worker, and persistence layers
**Method:** static code review of the cloud backend implementation and existing tests
**Verification limit:** runtime validation was not possible in this workspace because `dotnet` is not installed

## Summary

Overall risk is **HIGH**. The backend has solid structure, but I found several implementation defects that can break operational correctness under concurrency, make some failure paths non-recoverable, or remove data from the live system before archival is actually durable.

| Severity | Count |
|----------|-------|
| High | 6 |
| Medium | 1 |

## Findings

### HIGH 1. Bootstrap-token site limits can still be exceeded under moderate concurrency

**Evidence**

- `src/cloud/FccMiddleware.Application/Registration/GenerateBootstrapTokenHandler.cs:47-53` performs a pre-save count check only.
- `src/cloud/FccMiddleware.Application/Registration/GenerateBootstrapTokenHandler.cs:97-127` only performs the post-save corrective check when `activeCount >= MaxActiveTokensPerSite - 1`.

**Why this is a defect**

That conditional is incorrect. If a site has 3 active tokens and 3 requests race, all 3 requests pass the pre-check, all 3 save, and none of them execute the corrective re-check because `3 >= 4` is false. The system can end up with 6 active tokens even though the intended limit is 5.

**Impact**

- The bootstrap-token limit is not actually enforced.
- Multiple valid provisioning tokens can exist beyond policy.
- This weakens operational control over agent registration.

**Recommendation**

Enforce the limit with a database-backed serialization strategy, for example:

- advisory locking per `(legalEntityId, siteCode)`, or
- a transaction with `SERIALIZABLE` isolation and retry, or
- a dedicated counter/lease row updated atomically.

Do not rely on the current conditional post-save re-check.

### HIGH 2. DLQ persistence silently fails for XML and other non-JSON payload failures

**Evidence**

- `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:136-217` forwards raw Radix XML into the ingest pipeline.
- `src/cloud/FccMiddleware.Application/Ingestion/IngestTransactionHandler.cs:82-87`, `109-115`, and `137-143` pass `command.RawPayload` into `rawPayloadJson`.
- `src/cloud/FccMiddleware.Infrastructure/Persistence/Configurations/DeadLetterItemConfiguration.cs:26-28` maps `RawPayloadJson` to PostgreSQL `jsonb`.
- `src/cloud/FccMiddleware.Infrastructure/DeadLetter/DeadLetterService.cs:57-74` swallows any persistence exception and still returns an ID.

**Why this is a defect**

The code writes arbitrary raw payloads into a `jsonb` column. That works for valid JSON payloads, but raw XML is not valid JSON. When a Radix validation or normalization failure happens, DLQ persistence will fail at save time, and the exception is only logged. The original pipeline continues as if DLQ capture succeeded.

**Impact**

- Radix ingest failures can disappear entirely from the dead-letter queue.
- Operators lose the primary triage and recovery path for those failures.
- The code reports a dead-letter ID even when nothing was actually stored.

**Recommendation**

- Store raw payloads in a text column, or split storage into `raw_payload_text`, `raw_payload_json`, and `content_type`.
- Treat DLQ persistence failure as an observable failure signal, not a silent best-effort success.
- Add coverage for XML failure paths.

### HIGH 3. DLQ transaction replay cannot reliably reconstruct the original adapter request

**Evidence**

- `src/cloud/FccMiddleware.Domain/Entities/DeadLetterItem.cs:11-29` stores raw payload, site, and error metadata, but not vendor or content type.
- `src/cloud/FccMiddleware.Infrastructure/DeadLetter/DlqReplayService.cs:154-164` tries to infer `fccVendor` from the raw payload and defaults to `FccVendor.DOMS` when the field is absent.
- `src/cloud/FccMiddleware.Infrastructure/DeadLetter/DlqReplayService.cs:162-163` hardcodes `ContentType = "application/json"`.
- The original ingest paths send raw vendor payloads, not a normalized envelope with `fccVendor`; see `src/cloud/FccMiddleware.Api/Controllers/TransactionsController.cs:208-216`, `301-309`, and `399-407`.

**Why this is a defect**

Dead-letter replay assumes the stored raw payload contains enough metadata to recreate the original ingest request, but the dead-letter model does not persist that metadata. For many payloads the retry path will either:

- replay through the wrong adapter because it falls back to DOMS, or
- replay with the wrong content type.

**Impact**

- Manual retry from the portal is unreliable or broken for many transaction DLQ items.
- Replayed items can keep failing even when the underlying issue has been fixed.

**Recommendation**

Persist the original replay context when the dead-letter item is created:

- vendor
- content type
- ingestion source
- captured timestamp if required

Replay should use persisted metadata, not payload introspection plus a DOMS default.

### HIGH 4. Edge-upload batch processing is not atomic despite the handler claiming it is

**Evidence**

- `src/cloud/FccMiddleware.Application/Ingestion/UploadTransactionBatchHandler.cs:150-157` saves the transaction and outbox message first.
- `src/cloud/FccMiddleware.Application/Ingestion/UploadTransactionBatchHandler.cs:174-175` runs reconciliation and performs a second save afterward.

**Why this is a defect**

The transaction row is committed before reconciliation is applied. If reconciliation matching or the second save fails, the client can receive a failed upload path even though the transaction already exists in the database. A retry will then hit deduplication and return `DUPLICATE`, leaving the original record without the intended reconciliation state.

This is especially notable because the code comment at `:150` says the step is atomic, but it is not.

**Impact**

- Partial writes are possible in the edge-upload path.
- Client retries can become permanently non-healing duplicates.
- Reconciliation state can diverge between single-ingest and batch-upload paths.

**Recommendation**

Make each record truly atomic:

- stage transaction, reconciliation record, and outbox message together
- commit once inside a transaction

The single-ingest handler already follows the safer ordering pattern and is the better reference design.

### HIGH 5. The outbox worker can double-process messages when multiple worker replicas run

**Evidence**

- `src/cloud/FccMiddleware.Infrastructure/Events/OutboxPublisherWorker.cs:75-79` selects unprocessed rows with a plain `WHERE processed_at IS NULL ORDER BY id LIMIT ...`.
- `src/cloud/FccMiddleware.Infrastructure/Events/OutboxPublisherWorker.cs:129-147` writes the audit event and then sets `ProcessedAt`.
- `src/cloud/FccMiddleware.Infrastructure/Persistence/Configurations/OutboxMessageConfiguration.cs:13-35` defines no concurrency token or lease field on outbox rows.

**Why this is a defect**

Two worker instances can read the same unprocessed batch before either one marks the rows as processed. Because there is no row claiming, no `FOR UPDATE SKIP LOCKED`, and no optimistic concurrency guard, both workers can publish the same message.

**Impact**

- Duplicate audit events
- Duplicate downstream integrations once a real broker is added
- Non-idempotent event handling risk under horizontal scale-out

**Recommendation**

Introduce a real claim-and-process model, for example:

- `SELECT ... FOR UPDATE SKIP LOCKED`, or
- a `processing_started_at` / lease column updated atomically, or
- optimistic concurrency on `processed_at` with retry handling

### HIGH 6. Archive processing detaches live partitions before the archive is durably written

**Evidence**

- `src/cloud/FccMiddleware.Infrastructure/Workers/ArchiveWorker.cs:125` detaches the transaction partition before export.
- `src/cloud/FccMiddleware.Infrastructure/Workers/ArchiveWorker.cs:127-155` then reads, rewrites, serializes, and uploads the detached data.
- `src/cloud/FccMiddleware.Infrastructure/Workers/ArchiveWorker.cs:128` marks detached transaction rows as `ARCHIVED` before the object-store write succeeds.
- `src/cloud/FccMiddleware.Infrastructure/Workers/ArchiveWorker.cs:168-177` applies the same detach-before-export sequence to audit partitions.

**Why this is a defect**

Once the partition is detached, the live parent table no longer exposes that data. Any failure during Parquet serialization, manifest generation, or object-store upload leaves the system in a bad state: the partition is no longer live, but the archive is not guaranteed to exist.

**Impact**

- Historical data can disappear from normal application queries before archival is complete.
- Operational recovery becomes manual and fragile.
- This is a real data-availability risk, not just a cleanup bug.

**Recommendation**

Reverse the sequence:

1. read/export while the partition is still attached
2. verify archive write success
3. detach
4. optionally drop only after successful verification

If detaching first is required for operational reasons, add a durable recovery state machine and manifest-driven checkpointing before any destructive step.

### MEDIUM 7. Dashboard offline/degraded counts are wrong once more than 10 agents are offline

**Evidence**

- `src/cloud/FccMiddleware.Api/Controllers/AdminDashboardController.cs:101-121` builds `offlineAgents`, but truncates it with `Take(10)`.
- `src/cloud/FccMiddleware.Api/Controllers/AdminDashboardController.cs:131-132` uses `offlineAgents.Count` as the total offline count.

**Why this is a defect**

`offlineAgents` is clearly intended to be a sample list for display, but it is also reused as the source of the summary count. When 11 or more agents are offline, the API reports only 10 offline and pushes the remainder into the degraded count.

**Impact**

- Dashboard summary numbers become inaccurate.
- Ops users get a misleading view of fleet health.
- Alert triage can be skewed if teams trust the headline counts.

**Recommendation**

Compute:

- `offlineCount` from the full filtered set
- `offlineAgents` from a separate `Take(10)` display sample

Keep the summary count and display list as distinct calculations.

## Notes

- I did not make production code changes in this pass; this file contains the review findings only.
- I could not execute `dotnet test` or build verification here because the `dotnet` CLI is not installed in the current environment.
