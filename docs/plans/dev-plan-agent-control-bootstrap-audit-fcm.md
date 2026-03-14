# Agent Control + Bootstrap Token Audit + FCM — Implementation Plan

**Date:** 2026-03-14

**Use the relevant agent prompt per task area:**
- Cloud backend tasks: `docs/plans/agent-prompt-cloud-backend.md`
- Android edge-agent tasks: `docs/plans/agent-prompt-edge-agent.md`
- Desktop edge-agent tasks: `docs/plans/agent-prompt-desktop-edge-agent.md`
- Portal tasks: `docs/plans/agent-prompt-angular-portal.md`

**Sprint Cadence:** 2-week sprints

---

## Objectives

1. Add a complete, operator-friendly bootstrap token history with actor tracking, lifecycle visibility, and site-level filtering.
2. Add a shared cloud-to-agent command plane for Android and desktop agents.
3. Add Firebase Cloud Messaging support for Android as a low-latency wake-up path.
4. Preserve the existing offline-first architecture: polling remains the source of truth and the fallback path.
5. Improve the portal agent screens so operators can see active vs decommissioned agents and issue audited control actions.

## Non-Goals

- Do not send secrets, config payloads, or executable commands inside FCM payloads.
- Do not make FCM the only delivery path for Android commands.
- Do not add desktop push infrastructure in the first release; desktop remains poll-based.
- Do not add remote shell, OS reboot, or arbitrary command execution.
- Do not change the current registration flow to approval-gated onboarding in the MVP; suspicious-device quarantine is a later phase.

## Architecture Decisions

| Area | Decision |
|------|----------|
| Command source of truth | Add a cloud `agent_commands` table plus command fetch + ack APIs. |
| Android push | Use FCM data messages only as wake-up hints. Agent still fetches commands/config over authenticated HTTPS. |
| Desktop delivery | Poll the command endpoint on the existing cadence controller. |
| Config updates | Keep `GET /api/v1/agent/config` as the authoritative config channel. Push/poll only triggers a faster fetch. |
| Decommission | Existing server-side decommission remains authoritative. Command delivery improves UX speed but auth middleware remains the hard stop. |
| Reset semantics | Implement `RESET_LOCAL_STATE` as an app-controlled wipe of local data, overrides, and credentials, followed by provisioning mode. No OS factory reset. |
| Audit model | Every create, revoke, use, command issue, hint send, ack, and failure path writes audit events. |
| FCM hardware risk | Treat FCM as best-effort until validated on the physical Urovo i9100. Polling must remain sufficient on its own. |

## Proposed Data Model Additions

### Bootstrap token lifecycle

- Extend `bootstrap_tokens` with:
  - `revoked_at`
  - `revoked_by_actor_id`
  - `revoked_by_actor_display`
  - `created_by_actor_id` (nullable backfill-safe)
  - `created_by_actor_display` (nullable backfill-safe)
- Keep `used_at` and `used_by_device_id` as the authoritative usage link.
- Emit a new `BOOTSTRAP_TOKEN_USED` audit event with `tokenId`, `deviceId`, `siteCode`, `usedAt`.
- Compute effective token status in read APIs:
  - `ACTIVE` = `status == ACTIVE && expiresAt > now`
  - `EXPIRED` = `status == ACTIVE && expiresAt <= now`
  - `USED` = `status == USED`
  - `REVOKED` = `status == REVOKED`

### Agent command plane

- Add `agent_commands`:
  - `id`, `device_id`, `legal_entity_id`, `site_code`
  - `command_type`
  - `payload_json`
  - `status` (`PENDING`, `DELIVERY_HINT_SENT`, `ACKED`, `FAILED`, `EXPIRED`, `CANCELLED`)
  - `created_by_actor_id`, `created_by_actor_display`, `reason`
  - `created_at`, `expires_at`, `acked_at`, `updated_at`
  - `attempt_count`, `last_error`, `result_json`
- Add `agent_installations` for Android push registration:
  - `id`, `device_id`, `platform`, `push_provider`
  - `registration_token_ciphertext` or encrypted token blob
  - `token_hash` (for dedup lookup)
  - `last_seen_at`, `last_hint_sent_at`, `created_at`, `updated_at`

## MVP Command Types

| Command | Android | Desktop | Notes |
|---------|---------|---------|-------|
| `FORCE_CONFIG_PULL` | Yes | Yes | Fetch config immediately via the existing config endpoint. |
| `RESET_LOCAL_STATE` | Yes | Yes | Clear local DB, overrides, cached config, tokens, and return to provisioning mode. |
| `DECOMMISSION` | Yes | Yes | UX accelerator only; backend auth remains the hard enforcement layer. |

---

## Phase 0 — Viability + Contracts (Sprint 1)

### AC-0.1: Urovo FCM Viability Spike

**Prereqs:** None
**Estimated effort:** 2–3 days

**Task:**
Validate whether the Urovo i9100 build used in the field supports Firebase token issuance and reliable FCM data-message delivery.

**Detailed instructions:**
1. Verify Google Play Services / Firebase compatibility on the actual Urovo i9100 image used with Odoo POS.
2. Build a minimal Android spike that:
   - obtains an FCM registration token
   - receives a data-only message while app is foregrounded
   - receives a data-only message while app is backgrounded
   - receives or misses messages after process death / reboot
3. Measure wake-up latency and battery impact on the device.
4. Document failure modes:
   - no Play Services
   - token issuance blocked
   - background delivery too unreliable
5. Decide final rollout posture:
   - `required + supported`, or
   - `best-effort acceleration with polling fallback`

**Acceptance criteria:**
- A repo note records whether FCM is supported on the production Urovo image.
- Expected delivery guarantees and failure modes are documented.
- Polling-only fallback remains viable regardless of the outcome.

### AC-0.2: Shared Contracts and Audit Taxonomy

**Prereqs:** AC-0.1
**Estimated effort:** 1–2 days

**Task:**
Finalize the command types, command statuses, audit events, and API contracts before application work starts.

**Detailed instructions:**
1. Define cloud DTOs for:
   - bootstrap token history rows
   - agent command create request / response
   - edge command poll response
   - command ack request / response
   - Android installation token upsert request
2. Define new audit event names:
   - `BOOTSTRAP_TOKEN_USED`
   - `AGENT_COMMAND_CREATED`
   - `AGENT_COMMAND_ACKED`
   - `AGENT_COMMAND_FAILED`
   - `AGENT_PUSH_HINT_SENT`
   - `AGENT_PUSH_HINT_FAILED`
   - `AGENT_INSTALLATION_UPDATED`
3. Define payload redaction rules and sensitive-field handling.
4. Define command idempotency rules:
   - command IDs are globally unique
   - agent acks are idempotent
   - duplicate FCM hints are acceptable

**Acceptance criteria:**
- DTOs, statuses, and audit names are frozen before Phase 1 coding.
- Command semantics are unambiguous across Android and desktop agents.

---

## Phase 1 — Cloud Backend (Sprints 1–2)

### AC-1.1: Bootstrap Token Schema + Audit Enrichment

**Prereqs:** AC-0.2
**Estimated effort:** 2 days

**Task:**
Make bootstrap token lifecycle fully queryable and auditable.

**Detailed instructions:**
1. Add the new revoke and actor columns to `bootstrap_tokens`.
2. Backfill `created_by_actor_display` from the current `created_by` field where possible.
3. Update generate/revoke handlers to populate normalized actor fields.
4. Update registration to emit `BOOTSTRAP_TOKEN_USED` with `tokenId` and `deviceId`.
5. Add migration coverage and integration tests.

**Acceptance criteria:**
- A single bootstrap token row now contains enough metadata to render history without replaying audit logs.
- Token use is directly auditable by `tokenId`.

### AC-1.2: Bootstrap Token History Read APIs

**Prereqs:** AC-1.1
**Estimated effort:** 2 days

**Task:**
Expose token history to the portal.

**Detailed instructions:**
1. Add `GET /api/v1/admin/bootstrap-tokens` with filters:
   - `legalEntityId`
   - `siteCode`
   - `status`
   - `from`
   - `to`
   - cursor / pagination
2. Add `GET /api/v1/admin/bootstrap-tokens/{tokenId}` if needed for detail drill-down.
3. Return computed effective status and linked `usedByDeviceId`.
4. Keep `POST` and `DELETE` behavior unchanged for compatibility.

**Acceptance criteria:**
- Operators can retrieve all tokens for a site and distinguish active, expired, used, and revoked rows.
- The response includes who created and revoked each token.

### AC-1.3: Agent Command Control Plane

**Prereqs:** AC-0.2
**Estimated effort:** 4–5 days

**Task:**
Implement the authoritative command store and fetch/ack APIs.

**Detailed instructions:**
1. Add `agent_commands` schema, indexes, and EF configuration.
2. Add portal/admin API:
   - `POST /api/v1/admin/agents/{deviceId}/commands`
   - `GET /api/v1/agents/{deviceId}/commands`
3. Add edge-agent API:
   - `GET /api/v1/agent/commands`
   - `POST /api/v1/agent/commands/{commandId}/ack`
4. Enforce:
   - tenant scoping
   - `PortalAdminWrite` on command creation
   - idempotent ack handling
   - expiry checks
5. Emit audit events on create, ack, fail, expire, and cancel paths.

**Acceptance criteria:**
- Commands can be created, fetched once or repeatedly, and acked safely.
- Audit trail exists for the full command lifecycle.

### AC-1.4: Android Installation Tokens + FCM Hint Sender

**Prereqs:** AC-0.1, AC-0.2
**Estimated effort:** 3–4 days

**Task:**
Add Android push registration and FCM wake-up hint delivery from the cloud.

**Detailed instructions:**
1. Add `agent_installations` schema and encrypted storage for FCM registration tokens.
2. Add `POST /api/v1/agent/installations/android` to upsert the current FCM token for the authenticated device.
3. Implement a typed Firebase sender using FCM HTTP v1 and service-account credentials.
4. Send a data-only hint on:
   - new agent command creation
   - portal-triggered config publish / force config pull
5. Keep the FCM payload minimal:
   - `kind=command_pending` or `kind=config_changed`
   - `deviceId`
   - `commandCount` or `configVersion`
   - no secrets, no config payload, no command body
6. Treat sender failure as non-fatal:
   - log
   - audit
   - metrics
   - polling still succeeds later

**Acceptance criteria:**
- Android installations can register and rotate FCM tokens.
- Backend can send an FCM hint without exposing sensitive data.
- Failed hint delivery does not block command or config state changes.

### AC-1.5: Metrics, Feature Flags, and Tests

**Prereqs:** AC-1.1, AC-1.2, AC-1.3, AC-1.4
**Estimated effort:** 2–3 days

**Task:**
Harden the backend rollout path.

**Detailed instructions:**
1. Add feature flags:
   - `AgentCommands:Enabled`
   - `AgentCommands:FcmHintsEnabled`
   - `BootstrapTokens:HistoryApiEnabled`
2. Add metrics:
   - commands created / acked / failed / expired
   - push hints attempted / succeeded / failed
   - token history API latency
3. Add integration tests for:
   - token history filters
   - token use audit
   - command poll / ack
   - decommission command plus existing auth middleware enforcement
   - installation upsert auth
4. Add audit payload validation tests.

**Acceptance criteria:**
- The feature set can be toggled safely in non-production and production environments.
- Core control-plane flows are covered by integration tests.

---

## Phase 2 — Android Edge Agent (Sprints 2–3)

### AC-2.1: Firebase SDK + Installation Token Sync

**Prereqs:** AC-0.1, AC-1.4
**Estimated effort:** 2–3 days

**Task:**
Integrate Firebase Messaging into the Android agent and keep the installation token registered with the cloud.

**Detailed instructions:**
1. Add Firebase Messaging dependencies and `google-services` integration.
2. Implement `FirebaseMessagingService`:
   - handle `onNewToken`
   - queue token upsert when network is available
3. Persist only what is needed locally; treat the FCM token as sensitive.
4. Register or refresh the token on:
   - initial provisioning completion
   - app startup
   - token rotation callback
5. Add explicit no-op behavior when Firebase is unavailable on the device image.

**Acceptance criteria:**
- Android device can register its current FCM token with the cloud.
- Token rotation is handled without reprovisioning.
- App still functions when Firebase is unavailable.

### AC-2.2: Command Poll Worker + Executor

**Prereqs:** AC-1.3
**Estimated effort:** 4 days

**Task:**
Add a command poller and local executor to the Android agent.

**Detailed instructions:**
1. Add a `CommandPollWorker` under the existing cadence controller.
2. Fetch pending commands using the device JWT.
3. Implement idempotent execution for:
   - `FORCE_CONFIG_PULL`
   - `RESET_LOCAL_STATE`
   - `DECOMMISSION`
4. Post command ack with:
   - `SUCCEEDED`
   - `FAILED`
   - `IGNORED_ALREADY_APPLIED`
   - `IGNORED_EXPIRED`
5. `RESET_LOCAL_STATE` must:
   - stop runtime services safely
   - clear local DB and overrides
   - clear credentials
   - transition back to provisioning flow
6. `DECOMMISSION` must:
   - mark local decommission state immediately
   - stop runtime services
   - surface the decommissioned UI

**Acceptance criteria:**
- Android can execute and ack all MVP commands safely.
- Reset and decommission are crash-safe and idempotent.

### AC-2.3: FCM Wake-Up Path

**Prereqs:** AC-2.1, AC-2.2
**Estimated effort:** 2 days

**Task:**
Use FCM hints to accelerate config and command fetches.

**Detailed instructions:**
1. On `command_pending` hint:
   - wake the foreground service if needed
   - immediately poll commands
2. On `config_changed` hint:
   - immediately call the existing config poll path
3. Add local throttling so burst hints do not create request storms.
4. Fall back cleanly when hints arrive during no-network periods.

**Acceptance criteria:**
- Android command and config latency improves when FCM is available.
- No correctness depends on FCM delivery.

---

## Phase 3 — Desktop Edge Agent (Sprint 3)

### AC-3.1: Desktop Command Poll + Execution

**Prereqs:** AC-1.3
**Estimated effort:** 3 days

**Task:**
Add the same command-plane behavior to the desktop agent without push transport.

**Detailed instructions:**
1. Add a desktop command poll worker under the existing cadence controller.
2. Implement execution and ack for:
   - `FORCE_CONFIG_PULL`
   - `RESET_LOCAL_STATE`
   - `DECOMMISSION`
3. Reuse existing decommission and reprovisioning flows where possible.
4. Keep the desktop agent poll-only for this phase.

**Acceptance criteria:**
- Desktop agent reaches feature parity with Android for the MVP commands.
- No new always-on socket or push dependency is introduced.

### AC-3.2: Desktop UX + Safety Surface

**Prereqs:** AC-3.1
**Estimated effort:** 1–2 days

**Task:**
Surface command outcomes locally and keep destructive actions safe.

**Detailed instructions:**
1. Show local operator messaging for:
   - decommissioned state
   - reprovisioning required
   - reset in progress / completed
2. Ensure reset transitions the app to provisioning mode cleanly.
3. Add tests around duplicate or late command handling.

**Acceptance criteria:**
- Desktop operators are not left with a silently halted runtime after reset or decommission.

---

## Phase 4 — Portal (Sprints 3–4)

### AC-4.1: Bootstrap Token History UI — DONE

**Prereqs:** AC-1.2
**Estimated effort:** 3 days
**Status:** COMPLETE (2026-03-14)

**Task:**
Expose a site-level token history in the portal.

**Detailed instructions:**
1. Add a token history table to the bootstrap token area or a dedicated site-level screen.
2. Support filters:
   - legal entity
   - site
   - status
   - date range
3. Show columns:
   - token ID
   - effective status
   - created by / created at
   - revoked by / revoked at
   - used at / used by device
   - expiry
   - environment
4. Keep generate and revoke actions in the same workflow.

**Acceptance criteria:**
- An operator can answer who created a token, whether it is active, whether it was revoked, and which device consumed it.

**Implementation notes:**
- New `TokenHistoryComponent` at `/agents/token-history` route with full filter support (legal entity, site, status, date range).
- Token history table shows: token ID, site, effective status (color-coded tag), created by/at, revoked by/at, used at/by device, expiry.
- "Token History" button added to agent list header; "Generate Token" link in token history header.
- `BootstrapTokenHistoryRow` model and `getHistory()` service method call `GET /api/v1/admin/bootstrap-tokens`.
- Cursor-based pagination with "Load More" pattern matching existing components.

### AC-4.2: Agent Monitoring Inventory Improvements — DONE

**Prereqs:** AC-1.3
**Estimated effort:** 2 days
**Status:** COMPLETE (2026-03-14)

**Task:**
Make the agents screen reflect registration state clearly.

**Detailed instructions:**
1. Add a visible `status` column and backend-backed status filter.
2. Default the list to `ACTIVE`.
3. Keep offline/online health as a separate concern from active/decommissioned status.
4. Show command summary fields where useful:
   - last command type
   - last command status
   - last ack time

**Acceptance criteria:**
- The portal no longer conflates health state with registration status.
- Operators can view active and decommissioned agents explicitly.

**Implementation notes:**
- Status column (ACTIVE/DEACTIVATED) added to both offline and online agent tables with color-coded badges.
- Status filter dropdown added to Filters panel, defaulting to `ACTIVE`.
- `status` query param sent to `GET /api/v1/agents` backend endpoint.
- Command summary fields (last command type/status/ack time) are visible in the agent detail command history panel (AC-4.3) rather than the list view, since the backend list endpoint does not include these fields.
- Registration status badge added to agent detail header subtitle.

### AC-4.3: Command Issue + History UI — DONE

**Prereqs:** AC-1.3, AC-1.4
**Estimated effort:** 3 days
**Status:** COMPLETE (2026-03-14)

**Task:**
Add audited command issuance and history to the agent detail screen.

**Detailed instructions:**
1. Add command actions:
   - force config pull
   - reset local state
   - decommission
2. Require operator reason text for destructive commands.
3. Add a command history panel with lifecycle states and result details.
4. Show Android push-hint delivery status where available.

**Acceptance criteria:**
- Operators can issue and track commands without leaving the agent detail flow.
- Every destructive action is reasoned and auditable.

**Implementation notes:**
- "Issue Command" card on agent detail: dropdown for command type (FORCE_CONFIG_PULL, RESET_LOCAL_STATE, DECOMMISSION), reason textarea, destructive command warning, confirmation dialog.
- Destructive commands (RESET_LOCAL_STATE, DECOMMISSION) require min 10-char reason and browser confirmation.
- "Command History" card: paginated table showing type, status (color-coded tag), reason, created at, expires at, issued by.
- `AgentCommandService` with `createCommand()` and `getCommands()` methods.
- `AgentCommandRow` model matching `CreateAgentCommandResponse` contract.
- Command history auto-refreshes after issuing a command; decommission also triggers agent data refresh.

---

## Phase 5 — Optional Suspicious Device Workflow + Rollout (Sprint 4+)

### AC-5.1: Suspicious / Unidentified Device Quarantine

**Prereqs:** AC-1.3, AC-4.3
**Estimated effort:** 4–5 days

**Task:**
Add a later-phase approval or quarantine model for devices that should not become fully trusted immediately.

**Detailed instructions:**
1. Introduce optional registration states such as:
   - `PENDING_APPROVAL`
   - `QUARANTINED`
2. Trigger this only for defined policy breaches, for example:
   - unexpected serial replacement
   - site already occupied and replacement not approved
   - security rule mismatch
3. Add portal actions:
   - approve
   - reject
   - decommission
4. Keep this phase out of the MVP unless the business explicitly wants approval-gated onboarding.

**Acceptance criteria:**
- Suspicious devices can be held or revoked from the server side without becoming silently trusted.

### AC-5.2: Rollout, Runbooks, and Operational Hardening

**Prereqs:** All prior phases
**Estimated effort:** 2 days

**Task:**
Prepare the production rollout.

**Detailed instructions:**
1. Add operational runbooks for:
   - token history investigation
   - reset command recovery
   - decommission recovery
   - FCM token churn
   - FCM outage behavior
2. Add environment configuration and secret management for:
   - Firebase project ID
   - service-account credentials
   - feature flags
3. Define rollout order:
   - backend schema + APIs
   - portal token history
   - agent pollers
   - FCM acceleration
4. Add canary rollout and rollback steps.

**Acceptance criteria:**
- Production rollout can be performed incrementally and rolled back safely.

---

## Cross-Application Dependency Map

- `AC-1.1` must land before `AC-4.1`.
- `AC-1.3` must land before `AC-2.2`, `AC-3.1`, and `AC-4.3`.
- `AC-1.4` must land before `AC-2.1` and `AC-2.3`.
- `AC-0.1` is the gate for treating FCM as required versus best-effort.

## Recommended Delivery Order

1. Backend token lifecycle enrichment + history API (`AC-1.1`, `AC-1.2`)
2. Portal token history (`AC-4.1`)
3. Backend command plane (`AC-1.3`)
4. Android + desktop poll-based command execution (`AC-2.2`, `AC-3.1`)
5. Portal command UI (`AC-4.3`)
6. Android FCM registration + wake-up optimization (`AC-1.4`, `AC-2.1`, `AC-2.3`)
7. Optional quarantine workflow (`AC-5.1`)

## Key Risks

- Urovo device images may not support Firebase reliably; keep polling sufficient.
- Reset flows are destructive and must be idempotent, crash-safe, and well-audited.
- Command and token history payloads must avoid leaking secrets in logs, audit events, or push payloads.
- Portal UX must separate health state from registration state to avoid operator confusion.
