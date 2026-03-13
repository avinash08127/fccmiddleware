# Functional Findings

## Module: FleetManagement (Edge Agent Registration, Telemetry, Monitoring)

---

### FM-F01: GetEvents filters by EventType prefix "Agent" but audit events use "DEVICE_" prefix
- **Severity**: High
- **Location**: `AgentsController.cs:299-301`
- **Trace**: Portal → `GET /api/v1/agents/{id}/events` → `GetEvents()` → DB query
- **Description**: The events endpoint filters audit events where `EventType.StartsWith("Agent")` or matches `"ConnectivityChanged"` / `"BufferThresholdExceeded"`. However, the handlers write event types as `"DEVICE_REGISTERED"`, `"DEVICE_DECOMMISSIONED"`, `"DEVICE_TOKEN_REFRESHED"`, `"BOOTSTRAP_TOKEN_GENERATED"`, `"BOOTSTRAP_TOKEN_REVOKED"`, `"REFRESH_TOKEN_REUSE_DETECTED"`, and `"AgentHealthReported"`. Only `AgentHealthReported` matches the `StartsWith("Agent")` filter. All device lifecycle events (`DEVICE_*`, `BOOTSTRAP_TOKEN_*`, `REFRESH_TOKEN_REUSE_DETECTED`) are silently excluded from the timeline.
- **Impact**: Portal agent detail page shows no registration, decommission, token refresh, or token reuse events. Operators lose visibility into critical fleet lifecycle events.
- **Fix**: Change the filter to also match `StartsWith("DEVICE_")`, `StartsWith("BOOTSTRAP_TOKEN_")`, and `"REFRESH_TOKEN_REUSE_DETECTED"`.
- **Status**: ✅ FIXED — Replaced EventType-based filtering with `EntityId`-based lookup (`Where(item => item.EntityId == id)`). Added `EntityId = deviceId` to all agent-scoped audit event handlers: `SubmitTelemetryHandler`, `RegisterDeviceHandler`, `DecommissionDeviceHandler`, `RefreshDeviceTokenHandler` (both DEVICE_TOKEN_REFRESHED and REFRESH_TOKEN_REUSE_DETECTED). All event types are now returned for the agent.

### FM-F02: Connectivity timeline filters for "CONNECTIVITY_STATE_CHANGED" but telemetry handler writes "AgentHealthReported"
- **Severity**: Medium
- **Location**: `agent-detail.component.ts:673`
- **Trace**: Portal Detail → `connectivityEvents` computed signal
- **Description**: The connectivity timeline in the agent detail page filters events by `eventType === 'CONNECTIVITY_STATE_CHANGED'`, but no handler in the backend writes events with that type. The telemetry handler writes `"AgentHealthReported"`. The timeline will always be empty unless there's a separate process emitting `CONNECTIVITY_STATE_CHANGED` events.
- **Impact**: Connectivity timeline panel always shows "No connectivity events in the last 24 h" even when the agent's connectivity state has changed.
- **Fix**: Either add server-side connectivity-change detection that emits `CONNECTIVITY_STATE_CHANGED` audit events, or change the filter to derive state transitions from `AgentHealthReported` events.
- **Status**: ✅ FIXED — Added server-side connectivity-change detection in `SubmitTelemetryHandler`. When the telemetry snapshot's `ConnectivityState` differs from the incoming payload, a `CONNECTIVITY_STATE_CHANGED` audit event is emitted with `previousState`, `newState`, and `EntityId` set. The frontend filter now matches these events. First telemetry report is skipped to avoid false positives.

### FM-F03: Agent list fetches all agents (pageSize=500) without cursor-based pagination
- **Severity**: Medium
- **Location**: `agent-list.component.ts:520`
- **Trace**: Portal → `AgentService.getAgents({ pageSize: 500 })` → `GET /api/v1/agents`
- **Description**: The agent list component requests up to 500 agents in a single request and never follows the `nextCursor` for subsequent pages. If a legal entity has more than 500 agents, excess agents are silently dropped. The backend supports cursor-based pagination, but the frontend ignores it.
- **Impact**: Organizations with large fleets (>500 devices) see an incomplete agent list with no indication that more agents exist.
- **Fix**: Implement "load more" or infinite scroll using the `nextCursor` returned by the API.
- **Status**: ✅ FIXED — Page size reduced to 100. Frontend now tracks `nextCursor` and `hasMore` from API response. "Load More" button added with `exhaustMap` to prevent concurrent fetches. Total count and loaded count displayed in the table header.

### FM-F04: Agent list table labeled "All Agents" only shows online agents
- **Severity**: Low
- **Location**: `agent-list.component.ts:256, 495`
- **Trace**: Portal → `filteredOnline()` computed → template binding
- **Description**: The main agent table is labeled "All Agents" but is bound to `filteredOnline()`, which excludes all agents that `isAgentOffline()` returns true for. Offline agents appear only in the separate "Offline / Unreachable" section above. The label is misleading.
- **Impact**: Users may think they are seeing all agents when they are only seeing online ones.
- **Fix**: Change the label to "Online Agents" or bind to `allFiltered()`.
- **Status**: ✅ FIXED — The main table header now reads `Online Agents`, matching the `filteredOnline()` data source and the separate offline section.

### FM-F05: DecommissionDeviceHandler does not record `DecommissionedBy` in audit event
- **Severity**: Low
- **Location**: `DecommissionDeviceHandler.cs:46-62`, `AgentController.cs:338`
- **Trace**: Portal → `POST /api/v1/admin/agent/{deviceId}/decommission` → `DecommissionDeviceHandler`
- **Description**: The controller doesn't pass the authenticated user identity to the `DecommissionDeviceCommand`. The handler creates an audit event but doesn't record who performed the decommission. The `RevokeBootstrapTokenCommand` correctly includes `RevokedBy`, but the decommission flow doesn't have an equivalent.
- **Impact**: No audit trail of who decommissioned a device.
- **Status**: ✅ FIXED — `AgentController` now passes the acting portal user into `DecommissionDeviceCommand`, and `DecommissionDeviceHandler` writes `DecommissionedBy` into the `DEVICE_DECOMMISSIONED` audit payload.

### FM-F06: maxBatches parameter for diagnostic logs has no upper bound validation
- **Severity**: Low
- **Location**: `AgentController.cs:612-613`
- **Trace**: Portal → `GET /api/v1/agents/{deviceId}/diagnostic-logs?maxBatches=N`
- **Description**: The `maxBatches` query parameter defaults to 10 but has no upper-bound validation. A caller could pass `maxBatches=999999` to force the server to fetch an unbounded number of diagnostic log batches.
- **Impact**: Potential memory/performance issue if a device has uploaded many log batches.
- **Status**: ✅ FIXED — `AgentController.GetDiagnosticLogs()` now validates `maxBatches` and returns HTTP 400 unless it is between 1 and 100.

### FM-F07: Agent list siteCode filter is applied client-side only
- **Severity**: Low
- **Location**: `agent-list.component.ts:488, 520`
- **Trace**: Portal → `AgentService.getAgents()` → client-side filter
- **Description**: The siteCode filter in the agent list component filters the fetched array in memory (`allFiltered` computed). The backend API supports a `siteCode` query parameter for server-side filtering, but the frontend doesn't pass it. This means: (1) if there are >500 agents, the filter only applies to the first 500; (2) every refresh fetches all agents even when the user only cares about one site.
- **Impact**: Inaccurate filter results for large fleets; wasted bandwidth.
- **Status**: ✅ FIXED — The agent list now sends `siteCode` to `AgentService.getAgents()` for both initial refreshes and paginated `loadMore()` requests, so filtering is applied server-side.

---

## Module: Transactions

---

### TX-F01: Frontend transaction filter includes invalid status values "SYNCED" and "STALE_PENDING"
- **Severity**: High
- **Location**: `transaction-filters.component.ts` — statuses array
- **Trace**: Portal → TransactionFilters → `status` query param → `OpsTransactionsController.GetTransactions()` → `TryParseEnum<TransactionStatus>()`
- **Description**: The filter dropdown offers status values `SYNCED` and `STALE_PENDING`, but the backend `TransactionStatus` enum only defines `PENDING`, `SYNCED_TO_ODOO`, `DUPLICATE`, and `ARCHIVED`. When a user selects either of these invalid statuses, the API returns HTTP 400 with `VALIDATION.INVALID_STATUS`. The transaction state machine (`Transaction.Transition()`) confirms only four valid states.
- **Impact**: Two of six filter options are broken — users selecting "Synced" or "Stale Pending" see an error instead of results. Stale transactions can only be found via the `isStale` toggle, but the "Stale Pending" status option misleads users into thinking it's a status-based filter.
- **Fix**: Remove `SYNCED` and `STALE_PENDING` from the statuses array. If a "Stale Pending" convenience filter is desired, implement it as a compound filter (`status=PENDING&isStale=true`).
- **Status**: ✅ FIXED — Removed `SYNCED` and `STALE_PENDING` from `TransactionStatus` enum in `transaction.model.ts`, removed them from `statusOptions` in `transaction-filters.component.ts`, removed dead `case` branches in `transaction-list.component.ts` and `transaction-detail.component.ts` `txSeverity()`, and removed labels from `status-label.pipe.ts`.

### TX-F02: Radix CLOUD_DIRECT and webhook transactions are tagged as FCC_PUSH instead of their actual ingestion source
- **Severity**: Medium
- **Location**: `IngestTransactionHandler.cs:213`
- **Trace**: `TransactionsController.IngestRadixXml()` / `IngestPetroniteWebhook()` / `IngestAdvatecWebhook()` → `IngestTransactionCommand` → `IngestTransactionHandler` → `IngestionSource = IngestionSource.FCC_PUSH`
- **Description**: The `IngestTransactionHandler` hardcodes `IngestionSource = IngestionSource.FCC_PUSH` for every transaction it processes, regardless of the actual ingestion path. Radix CLOUD_DIRECT XML push, Petronite webhook, and Advatec webhook all enter through distinct controller actions with different auth mechanisms, but all are recorded as `FCC_PUSH`. The `IngestTransactionCommand` does not carry an `IngestionSource` field.
- **Impact**: Portal filters by ingestion source are inaccurate — operators cannot distinguish between edge-relayed FCC pushes and direct cloud-to-cloud webhook integrations. The frontend filter option `CLOUD_PULL` (from transaction-filters.component.ts) will never match any records. Dashboard volume-by-source charts misattribute traffic.
- **Fix**: Add `IngestionSource` to `IngestTransactionCommand` and set it appropriately in each controller action (e.g., `CLOUD_DIRECT` for Radix XML, `WEBHOOK` for Petronite/Advatec).
- **Status**: ✅ FIXED — Added `CLOUD_DIRECT` and `WEBHOOK` to backend `IngestionSource` enum. Added `IngestionSource` property to `IngestTransactionCommand` (defaults to `FCC_PUSH`). Updated `IngestTransactionHandler` to use `command.IngestionSource`. Set `CLOUD_DIRECT` in `IngestRadixXml()`, `WEBHOOK` in `IngestPetroniteWebhook()` and `IngestAdvatecWebhook()`. Updated frontend enum, filter options, and status-label pipe.

### TX-F03: Reconciliation results can be lost if the process crashes between the two SaveChangesAsync calls
- **Severity**: Medium
- **Location**: `IngestTransactionHandler.cs:241, 269-270`
- **Trace**: Ingestion → `SaveChangesAsync()` (commits transaction + outbox) → `MatchAsync()` (modifies reconciliation state) → `SaveChangesAsync()` (commits match results)
- **Description**: After the first `SaveChangesAsync` persists the transaction and outbox message, `_reconciliationMatchingService.MatchAsync` runs and potentially creates a `ReconciliationRecord` and updates `transaction.ReconciliationStatus`. The second `SaveChangesAsync` persists these changes. If the application crashes, throws, or the database connection drops between lines 269 and 270, the transaction is committed to the database but its reconciliation match results are silently lost. No retry mechanism or outbox event exists for reconciliation matching.
- **Impact**: Intermittent reconciliation gaps — some transactions may never be matched against pre-auth records, causing phantom "unmatched" exceptions.
- **Fix**: Either wrap both operations in a single `SaveChangesAsync`, or trigger reconciliation matching from the outbox worker (event-driven, retry-safe).
- **Status**: ✅ FIXED — Moved `_reconciliationMatchingService.MatchAsync()` before the single `SaveChangesAsync()` in `IngestTransactionHandler`, so the transaction, reconciliation result, and outbox event are committed atomically. Removed the second `SaveChangesAsync` call.

### TX-F04: OpsTransactionsController.Acknowledge allows acknowledge attempt with non-existent transaction IDs
- **Severity**: Low
- **Location**: `OpsTransactionsController.cs:329-356`
- **Trace**: Portal → `POST /api/v1/ops/transactions/acknowledge` → `Acknowledge()`
- **Description**: When all submitted transaction IDs are non-existent (`legalEntityIds.Count == 0`), the controller falls back to using the user's single scoped legal entity ID (line 343-345). The command then executes against the scoped entity, and the handler will return NOT_FOUND for each item. While functionally harmless, this path allows the acknowledge command to execute even when zero transactions were found, creating unnecessary work and audit noise.
- **Impact**: Minor — the handler correctly reports NOT_FOUND per item, but the fallback logic obscures the fact that no valid targets existed.
- **Status**: ✅ FIXED — Replaced the fallback to scoped legal entity with an early `400 VALIDATION.NO_TRANSACTIONS_FOUND` return when `legalEntityIds.Count == 0`, preventing unnecessary command execution and audit noise.

### TX-F05: Fuzzy match detection only considers PENDING transactions — misses recently acknowledged duplicates
- **Severity**: Low
- **Location**: `FccMiddlewareDbContext.cs:160-171` (`HasFuzzyMatchAsync`)
- **Trace**: Ingestion → `IngestTransactionHandler` → `HasFuzzyMatchAsync()` → DB query with `Status == TransactionStatus.PENDING`
- **Description**: The fuzzy match query (same pump, nozzle, amount, within ±5s window) only matches against transactions in `PENDING` status. If a near-identical transaction was already acknowledged by Odoo (`SYNCED_TO_ODOO`), the new incoming transaction will not be flagged for review. This creates a window where a genuine duplicate that arrived after the original was acknowledged passes through without the `REVIEW_FUZZY_MATCH` flag.
- **Impact**: Potential for undetected fuzzy duplicates when Odoo acknowledgement races with ingestion of near-identical transactions.
- **Status**: ✅ FIXED — Changed `HasFuzzyMatchAsync` filter from `Status == PENDING` to `Status != DUPLICATE`. Now matches against all non-duplicate statuses (PENDING, SYNCED_TO_ODOO, ARCHIVED), so recently acknowledged transactions are still detected as potential fuzzy duplicates. Updated interface docstring accordingly.

### TX-F06: Frontend ingestion source filter includes "CLOUD_PULL" which likely has no matching enum value
- **Severity**: Low
- **Location**: `transaction-filters.component.ts` — ingestionSourceOptions
- **Trace**: Portal → TransactionFilters → `ingestionSource` query param → API `TryParseEnum<IngestionSource>()`
- **Description**: The filter includes `CLOUD_PULL` as an ingestion source option. Combined with TX-F02 (all ingested transactions hardcoded as `FCC_PUSH`), this filter option will either fail validation (if `CLOUD_PULL` doesn't exist in the enum) or return zero results (if it does exist but no transactions use it).
- **Impact**: Misleading filter option that never returns results.
- **Status**: ✅ FIXED — Removed `CLOUD_PULL` from `ingestionSourceOptions` in `transaction-filters.component.ts`. The enum value is retained in both backend and frontend for future use, but the filter dropdown no longer offers it since no ingestion path currently produces `CLOUD_PULL` transactions.

---

## Module: Reconciliation

---

### RC-F01: REVIEW_FUZZY_MATCH status exists in domain enum but rejected by DB check constraint
- **Severity**: High
- **Location**: `ReconciliationStatus.cs:16`, `ReconciliationRecordConfiguration.cs:64-66`
- **Trace**: Domain enum → EF string conversion → PostgreSQL CHECK constraint
- **Description**: The `ReconciliationStatus` enum defines `REVIEW_FUZZY_MATCH` (ordinal 6), but the database check constraint only allows `('UNMATCHED','MATCHED','VARIANCE_WITHIN_TOLERANCE','VARIANCE_FLAGGED','APPROVED','REJECTED')`. If any code path sets a record's status to `REVIEW_FUZZY_MATCH`, PostgreSQL will reject the INSERT/UPDATE with a check constraint violation. The status is present in the enum but unusable.
- **Impact**: Any future code that uses this status will produce a runtime DB exception. If it's currently used anywhere (e.g., fuzzy match detection in the ingestion pipeline), records are silently lost.
- **Fix**: Either add `'REVIEW_FUZZY_MATCH'` to the check constraint, or remove the unused enum value.
- **Status**: ✅ FIXED — Added `'REVIEW_FUZZY_MATCH'` to the `chk_reconciliation_status` CHECK constraint in `ReconciliationRecordConfiguration.cs`. The constraint now allows all seven enum values. Note: a database migration will be required to alter the existing constraint in deployed environments.

### RC-F02: ApplyMatchedOutcome silently force-overrides terminal pre-auth states to COMPLETED
- **Severity**: High
- **Location**: `ReconciliationMatchingService.cs:453-460`
- **Trace**: Ingestion → `MatchAsync()` → `ApplyMatchedOutcome()` → `preAuth.Transition(COMPLETED)` → catch → `preAuth.Status = COMPLETED`
- **Description**: When the reconciliation engine matches a transaction to a pre-auth, it calls `preAuth.Transition(PreAuthStatus.COMPLETED)`. If the transition is invalid (e.g., the pre-auth is already CANCELLED, EXPIRED, or FAILED — all terminal states per the state machine), the catch block silently force-sets `preAuth.Status = PreAuthStatus.COMPLETED` anyway. This bypasses the domain state machine entirely. A pre-auth that expired 12 hours ago and then gets matched to a late-arriving transaction will be silently flipped from EXPIRED to COMPLETED, with no audit trail of the override.
- **Impact**: Corrupts pre-auth lifecycle history. Expired/cancelled pre-auths appear as completed in the portal. Compliance reports show inflated completion rates. The state machine exists specifically to prevent invalid transitions but is bypassed here.
- **Fix**: Log the invalid transition instead of silently overriding. Consider a distinct status like `COMPLETED_LATE` or flag the reconciliation record as `matchedPostTerminal=true`.

### RC-F03: Variance calculations use different denominator amounts for the same record
- **Severity**: Medium
- **Location**: `ReconciliationMatchingService.cs:406-449`
- **Trace**: Matching → `ApplyMatchedOutcome()` → variance fields on ReconciliationRecord vs PreAuthRecord
- **Description**: The reconciliation record's `VariancePercent` is calculated against `preAuth.AuthorizedAmountMinorUnits` (what the FCC device approved), while the pre-auth record's `VarianceBps` is calculated against `preAuth.RequestedAmountMinorUnits` (what was originally requested). For partial authorizations where the FCC authorizes less than requested, these denominators differ, producing different variance percentages for the same dispense event. The portal DTO `VarianceBps` maps from the reconciliation record's `VariancePercent * 100`, using the authorized amount as base. But the pre-auth detail view uses `preAuth.VarianceBps` based on the requested amount.
- **Impact**: Variance displayed in the reconciliation workbench differs from variance shown in pre-auth detail for partial authorizations. Operators may see conflicting variance figures depending on which view they use.
- **Fix**: Standardize on one denominator (either always AuthorizedAmount or always RequestedAmount) and document the convention.

### RC-F04: Approve/reject action buttons hidden for SystemAdministrator and Auditor despite route access
- **Severity**: Medium
- **Location**: `reconciliation-detail.component.ts:276`, `reconciliation.routes.ts:16`
- **Trace**: Portal → route guard → detail component → `appRoleVisible` directive
- **Description**: The reconciliation routes grant access to `['SystemAdmin', 'SystemAdministrator', 'OperationsManager', 'SiteSupervisor', 'Auditor']`. However, the approve/reject action buttons in the detail component use `*appRoleVisible="['SystemAdmin', 'OperationsManager']"`. If the backend `PortalReconciliationReview` policy allows `SystemAdministrator` to review, those users can navigate to the detail page but cannot see the action buttons. They would need to use the API directly. Similarly, if Auditors or SiteSupervisors are meant to review (per the policy), the UI hides those controls.
- **Impact**: Role-to-action mismatch. Users with valid API-level permissions see a read-only view with no indication that they can take action.
- **Fix**: Align the `appRoleVisible` roles with the backend `PortalReconciliationReview` policy roles.

### RC-F05: No minimum reason length enforced on backend — frontend 10-char minimum is client-only
- **Severity**: Low
- **Location**: `OpsReconciliationController.cs:283-285`, `ReviewReconciliationHandler.cs:32-37`, `reconciliation-detail.component.ts:350`
- **Trace**: Portal → `POST /api/v1/ops/reconciliation/{id}/approve` → controller → handler
- **Description**: The frontend enforces a 10-character minimum for the review reason (`reason().length < 10`). The backend only checks `IsNullOrWhiteSpace(request.Reason)` — any non-empty string (even a single space followed by a character) passes validation. An API caller bypassing the frontend can submit a 1-character reason. The DB column allows up to 1000 chars but has no minimum.
- **Impact**: Audit records may contain meaningless reasons like "ok" or "x", defeating the purpose of mandatory justification for compliance.
- **Fix**: Add `reason.Length < 10` validation in the handler alongside the empty check.

### RC-F06: Reason length has no maximum validation — DB truncation or exception on >1000 chars
- **Severity**: Low
- **Location**: `ReviewReconciliationHandler.cs:32`, `ReconciliationRecordConfiguration.cs:45`
- **Trace**: Handler → `reason.Trim()` → save → DB column `review_reason VARCHAR(1000)`
- **Description**: The handler trims the reason but never checks its length. The DB column `review_reason` has `HasMaxLength(1000)`. If a reason exceeds 1000 characters after trimming, the behavior depends on the database provider: PostgreSQL will raise an error, causing a 500 response. No user-friendly validation message is returned.
- **Impact**: Unexpected 500 error for very long review reasons.
- **Fix**: Add `reason.Length > 1000` check in the handler returning a validation error.
- **Status**: ✅ FIXED — Added `reason.Length > 1000` validation check in `ReviewReconciliationHandler` after the empty-check, returning `VALIDATION.REASON_TOO_LONG` error code with a user-friendly message.

## Module: PreAuthorization (Odoo POS → Edge Agent → FCC Device → Cloud)

---

### PA-F01: Desktop edge agent stores Odoo pump/nozzle numbers but Android stores FCC pump/nozzle — data inconsistency in cloud
- **Severity**: High
- **Location**: Desktop `PreAuthHandler.cs:146-147`, Android `PreAuthHandler.kt:157-158`
- **Trace**: Odoo POS → Edge Agent → record creation → cloud forward → `pre_auth_records.pump_number/nozzle_number`
- **Description**: Desktop `PreAuthHandler.cs` persists `request.OdooPumpNumber` and `request.OdooNozzleNumber` in the local record (`record.PumpNumber = request.OdooPumpNumber`). Android `PreAuthHandler.kt` persists the translated FCC numbers (`pumpNumber = nozzle.fccPumpNumber, nozzleNumber = nozzle.fccNozzleNumber`). When both forward to cloud via `POST /api/v1/preauth`, the same logical pre-auth has different pump/nozzle values depending on which agent type created it. The cloud domain entity makes no distinction between Odoo and FCC pump numbers.
- **Impact**: Reconciliation matching by pump+nozzle produces inconsistent results across agent types. Portal shows Odoo-facing numbers for desktop-originated pre-auths and FCC-facing numbers for Android-originated ones.
- **Fix**: Standardise both agents to store FCC pump/nozzle numbers in the pre-auth record (matching the cloud domain model). Desktop should use `nozzle.FccPumpNumber` / `nozzle.FccNozzleNumber` like Android does.
- **Status**: ✅ FIXED — Changed Desktop `PreAuthHandler.cs` lines 146-147 from `request.OdooPumpNumber`/`request.OdooNozzleNumber` to `nozzle.FccPumpNumber`/`nozzle.FccNozzleNumber`, matching the Android implementation. Both agents now consistently store FCC pump/nozzle numbers.

### PA-F02: Desktop terminal re-request resets record in-place, destroying terminal-state audit trail
- **Severity**: High
- **Location**: Desktop `PreAuthHandler.cs:130-171`
- **Trace**: Odoo POS → `POST /api/v1/preauth` → HandleAsync → existing terminal record → in-place reset → SaveChangesAsync
- **Description**: When a pre-auth reaches a terminal state (Completed/Cancelled/Expired/Failed) and a new request arrives for the same `(OdooOrderId, SiteCode)`, the desktop handler reuses the existing record object, clearing all timestamps (lines 163-169: `FailureReason = null, FccCorrelationId = null, AuthorizedAt = null, CancelledAt = null, ExpiredAt = null, FailedAt = null`). The original terminal state data is permanently overwritten. This is caused by the desktop's unconditional unique index `ix_par_idemp` on `(OdooOrderId, SiteCode)` which prevents a second record from being inserted. The cloud uses a filtered unique index (`status IN ('PENDING','AUTHORIZED','DISPENSING')`) allowing terminal records to coexist with new ones.
- **Impact**: Terminal pre-auth history is permanently lost on the desktop edge agent. Cloud may have stale terminal record data if the old state was forwarded. Audit trail for failed/cancelled pre-auths is destroyed.
- **Fix**: Either (a) delete the terminal record before inserting a new one, or (b) implement SQLite triggers or application-level logic to archive terminal records before reset.

### PA-F03: ForwardPreAuthHandler idempotent path silently drops mutable field updates
- **Severity**: Medium
- **Location**: `ForwardPreAuthHandler.cs:67-84`
- **Trace**: Edge Agent → `POST /api/v1/preauth` (retry with updated fields) → ForwardPreAuthHandler → `existing.Status == command.Status` → early return
- **Description**: When the edge agent re-sends a pre-auth forward with the same status (e.g., AUTHORIZED retry after network failure), the handler returns the existing record immediately without calling `ApplyMutableFields`. If the retry includes updated `FccCorrelationId`, `CustomerName`, or `FccAuthorizationCode`, those values are silently ignored. In contrast, `UpdatePreAuthStatusHandler.cs:39-45` correctly calls `ApplyFields` in its idempotent path.
- **Impact**: Field updates arriving via retried forwards are silently lost. The cloud record may lack FCC correlation IDs needed for reconciliation matching.
- **Fix**: Call `ApplyMutableFields(existing, command)` before returning in the idempotent path, then `SaveChangesAsync` if any field changed.

### PA-F04: Cloud controller missing PumpNumber and NozzleNumber positive validation
- **Severity**: Medium
- **Location**: `PreAuthController.cs:76-89`
- **Trace**: Edge Agent → `POST /api/v1/preauth` → PreAuthController → ForwardPreAuthCommand → `pre_auth_records`
- **Description**: The controller validates `RequestedAmount > 0` (line 77) and `UnitPrice > 0` (line 84) but performs no validation on `PumpNumber` or `NozzleNumber`. These fields are `int` on the DTO (`PreAuthForwardRequest.cs:15-16`) and default to 0 if omitted from JSON. The database has no check constraint on these columns either. Edge agents validate these fields, but a malformed or crafted request could insert records with zero or negative pump/nozzle numbers.
- **Impact**: Invalid pump/nozzle records in the database cause reconciliation matching failures and confusing portal displays.
- **Fix**: Add `PumpNumber > 0` and `NozzleNumber > 0` validation in the controller, and add DB check constraints.

### PA-F05: Cloud controller missing ExpiresAt temporal validation
- **Severity**: Medium
- **Location**: `PreAuthController.cs:93-115`
- **Trace**: Edge Agent → `POST /api/v1/preauth` → PreAuthController → ForwardPreAuthCommand → record created → ExpiryWorker picks up immediately
- **Description**: The controller accepts `ExpiresAt` and `RequestedAt` without validating `ExpiresAt > RequestedAt` or that `ExpiresAt` is not in the past. A record with `ExpiresAt` in the past would be immediately picked up by `PreAuthExpiryWorker` on its next 60-second tick and transitioned to EXPIRED. If `ExpiresAt < RequestedAt`, the record is logically invalid but accepted.
- **Impact**: Pre-auth records created with past expiry are immediately expired by the worker, potentially triggering unnecessary FCC deauthorization calls. Records with inverted timestamps confuse the audit trail.
- **Fix**: Add validation: `ExpiresAt > RequestedAt` and optionally `ExpiresAt > DateTimeOffset.UtcNow`.

### PA-F06: Android cloud forward worker omits vehicleNumber, customerBusinessName, and attendantId from forward request
- **Severity**: Medium
- **Location**: `PreAuthCloudForwardWorker.kt:260-277` (`toForwardRequest()`)
- **Trace**: Odoo POS → PreAuthHandler → Room `pre_auth_records` → PreAuthCloudForwardWorker → `POST /api/v1/preauth` → cloud record missing fields
- **Description**: The `toForwardRequest()` extension function maps 16 fields from the Room entity to the cloud request but omits three fields that both the Room entity stores and the cloud `PreAuthForwardRequest` contract accepts: `vehicleNumber`, `customerBusinessName`, and `attendantId`. These fields reach the Android edge agent from Odoo POS but are never forwarded to cloud.
- **Impact**: Cloud pre-auth records have `null` for vehicleNumber, customerBusinessName, and attendantId even when the edge agent collected them. Portal users and reconciliation reports lack these details for Android-originated pre-auths.
- **Fix**: Add the three missing fields to the `toForwardRequest()` mapping.

### PA-F07: Android handler creates PreAuthRecord with customerName = null instead of using command value
- **Severity**: Medium
- **Location**: `PreAuthHandler.kt:153-180` (line 168: `customerName = null`)
- **Trace**: Odoo POS → `PreAuthCommand(customerName = "...")` → PreAuthHandler → `PreAuthRecord(customerName = null)` → cloud forward → `customerName = null`
- **Description**: The `PreAuthCommand` data class includes a `customerName` field (`AdapterTypes.kt`), but when `PreAuthHandler.handle()` creates the Room `PreAuthRecord`, it hardcodes `customerName = null` (line 168) instead of using `command.customerName`. Notably, `customerTaxId` IS correctly mapped on line 169 (`customerTaxId = command.customerTaxId`). Since the cloud forward worker reads `customerName` from the Room entity, the null propagates all the way to the cloud record.
- **Impact**: Customer name is permanently lost for all Android-originated pre-auths. Portal shows no customer name on pre-auth detail views.
- **Fix**: Change line 168 to `customerName = command.customerName`.

---

## Module: Onboarding (Registration & Provisioning)

---

### OB-F01: Android provisioning always sends `replacePreviousAgent=false` — no UI to replace existing active agent
- **Severity**: Medium
- **Location**: `ProvisioningViewModel.kt:112-119`
- **Trace**: Android ProvisioningActivity → ProvisioningViewModel.register() → `buildRegistrationRequest()` → `DeviceRegistrationRequest(...)` → cloud `POST /api/v1/agent/register`
- **Description**: The `buildRegistrationRequest()` method constructs a `DeviceRegistrationRequest` with six fields (provisioningToken, siteCode, deviceSerialNumber, deviceModel, osVersion, agentVersion) but never includes `replacePreviousAgent`. The data class defaults to `false`. The cloud `RegisterDeviceHandler` returns `ACTIVE_AGENT_EXISTS` ("Set replacePreviousAgent=true to replace") when an active agent already exists for the site. The Android provisioning UI (both QR scan and manual entry paths) has no checkbox, toggle, or prompt to set this flag. The QR code payload (`QrBootstrapData`) also does not carry this field.
- **Impact**: If a site already has an active agent (e.g., old device not yet decommissioned), a new Android device cannot register for that site. The user sees "Registration rejected: ACTIVE_AGENT_EXISTS" with instructions to set a flag they cannot set. The only workaround is to decommission the old agent from the portal first, creating a multi-step cross-system workflow for what should be a single provisioning operation.
- **Fix**: Add a `replacePreviousAgent` parameter to the registration flow. Options: (1) prompt the user on `ACTIVE_AGENT_EXISTS` error with a "Replace existing agent?" dialog and retry with the flag set, or (2) always set `replacePreviousAgent=true` for QR-scan provisioning since the admin generated a new token, implying intent to replace.

### OB-F02: Android re-provisioning does not clear Room database — stale buffered data persists under new registration
- **Severity**: Medium
- **Location**: `DecommissionedActivity.kt:53-63` (`startReProvisioning`)
- **Trace**: Decommissioned state → "Re-Provision Device" → `keystoreManager.clearAll()` + `encryptedPrefs.clearAll()` → ProvisioningActivity → new registration → EdgeAgentForegroundService starts
- **Description**: When a user initiates re-provisioning from the decommissioned state, `startReProvisioning()` (line 53) clears Keystore keys and EncryptedPrefs but does NOT clear the Room database (`BufferDatabase`). Room tables including `transaction_buffer`, `pre_auth_records`, and `agent_configs` retain data from the previous registration. After re-provisioning to a new site, the `CloudUploadWorker` will attempt to upload old buffered transactions (from the previous site) using the new device JWT. The cloud would either reject them (site mismatch) or, if the device re-registers at the same site, accept stale transactions as new uploads. Similarly, `ProvisioningViewModel.handleRegistrationSuccess()` (line 133-134) clears Keystore and EncryptedPrefs but also skips Room cleanup.
- **Impact**: Cross-site data leakage if device re-provisions to a different site. Duplicate transaction uploads if re-provisioning to the same site. Stale pre-auth records from the old registration may interfere with the new registration's pre-auth handling.
- **Fix**: Add `BufferDatabase.clearAllTables()` (Room's built-in table clearing) to the re-provisioning flow in both `DecommissionedActivity.startReProvisioning()` and `ProvisioningViewModel.handleRegistrationSuccess()`.

### OB-F03: GetAgentConfigHandler.BuildMappingsDto has no null check on `nozzle.Product` — NullReferenceException for orphaned nozzles
- **Severity**: Medium
- **Location**: `GetAgentConfigHandler.cs` — `BuildMappingsDto()` method, inside nested foreach loop
- **Trace**: Edge Agent → `GET /api/v1/agent/config` → `GetAgentConfigHandler` → `BuildMappingsDto(site)` → `var product = nozzle.Product; product.ProductCode`
- **Description**: The `BuildMappingsDto` method iterates through active pumps and nozzles, then accesses `nozzle.Product.ProductCode` without a null guard. The eager loading query (`GetFccConfigWithSiteDataAsync`) uses `.ThenInclude(n => n.Product)`, but if a nozzle's product FK is null or the referenced product was soft-deleted, the `Product` navigation will be null. This causes a `NullReferenceException` that propagates as a 500 error on the config endpoint, blocking ALL edge agents registered at that site from receiving configuration updates.
- **Impact**: A single orphaned nozzle-product relationship prevents all agents at the site from receiving config. Since config polls run every 5 minutes, the agents operate on the last-known-good config indefinitely with no explicit alert. The site admin must fix the nozzle-product mapping before config delivery resumes.
- **Fix**: Add `if (product is null) continue;` before accessing product properties, and log a warning for the orphaned nozzle.

### OB-F04: Bootstrap token generation audit event omits `Environment` field
- **Severity**: Low
- **Location**: `GenerateBootstrapTokenHandler.cs:75-86` (audit payload)
- **Trace**: Portal → `POST /api/v1/admin/bootstrap-tokens` → `GenerateBootstrapTokenHandler` → audit event payload
- **Description**: The audit event payload for `BOOTSTRAP_TOKEN_GENERATED` includes `TokenId`, `SiteCode`, `CreatedBy`, and `ExpiresAt`, but does not include the `Environment` field despite it being set on the token entity (`token.Environment = request.Environment`). For multi-environment deployments (production, staging, development), the audit trail does not record which cloud environment the bootstrap token targets. The `BOOTSTRAP_TOKEN_REVOKED` event also omits this field.
- **Impact**: Auditors reviewing token generation history cannot determine which environment a token was generated for. In a mixed-environment fleet, this makes it harder to investigate provisioning issues or security incidents.
- **Fix**: Add `Environment = request.Environment` to the audit event payload in both `GenerateBootstrapTokenHandler` and `RevokeBootstrapTokenHandler`.

### OB-F05: Decommission endpoint does not validate that the authenticated user has access to the device's legal entity before dispatching the command — relies on pre-query for tenant check
- **Severity**: Low
- **Location**: `AgentController.cs:325-340`
- **Trace**: Portal → `POST /api/v1/admin/agent/{deviceId}/decommission` → pre-query → `access.CanAccess()` → `DecommissionDeviceCommand` → handler
- **Description**: The controller fetches the device's LegalEntityId via a separate pre-query (line 325-330), checks `access.CanAccess()` (line 335), then dispatches `DecommissionDeviceCommand` with only `DeviceId`. The handler independently loads the device (another DB query) and decommissions it without any tenant check. If the pre-query and the handler load are not within the same transaction, a TOCTOU race exists where the device's LegalEntityId could change between the pre-query check and the handler's load (theoretically, if LegalEntityId is mutable). In practice, LegalEntityId is immutable after registration, so this is not exploitable — but the pattern violates defense-in-depth. The handler trusts that the caller already verified access.
- **Impact**: Minimal — LegalEntityId is immutable. Flagged as a defense-in-depth architectural note.
