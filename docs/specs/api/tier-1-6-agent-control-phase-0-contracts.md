# Phase 0 Agent Control Contracts

## 1. Output Location
- Target file path: `docs/specs/api/tier-1-6-agent-control-phase-0-contracts.md`
- Related code paths:
  - `src/cloud/FccMiddleware.Contracts/AgentControl`
  - `src/cloud/FccMiddleware.Domain/Enums`
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt`
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/Models/AgentControlModels.cs`

## 2. Scope
- Plan items addressed:
  - `AC-0.2: Shared Contracts and Audit Taxonomy`
  - the contract portion of `AC-0.1` needed to keep Android FCM optional and non-authoritative
- In scope:
  - bootstrap token history row shape
  - agent command create/poll/ack DTOs
  - Android installation token upsert DTO
  - command types, command statuses, audit event names
  - payload redaction and idempotency rules
- Out of scope:
  - database schema and handlers (`Phase 1`)
  - portal screens (`Phase 3`)
  - Android command execution logic (`Phase 2`)

## 3. Source Traceability
- Requirements and plans:
  - `docs/plans/dev-plan-agent-control-bootstrap-audit-fcm.md`
  - `docs/EdgeAgentRegistration.md`
  - `docs/analysis/EdgeAgentRegistrationAnalysis.md`
- Existing implementation patterns reused:
  - `src/cloud/FccMiddleware.Contracts/Registration`
  - `src/cloud/FccMiddleware.Contracts/Portal`
  - `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt`
  - `src/desktop-edge-agent/src/FccDesktopAgent.Core/Registration/RegistrationModels.cs`

## 4. Frozen Decisions

### 4.1 Command Types

| Value | Android | Desktop | Meaning |
|---|---|---|---|
| `FORCE_CONFIG_PULL` | Yes | Yes | Fetch cloud config immediately; this does not embed config data in the command. |
| `RESET_LOCAL_STATE` | Yes | Yes | Clear local data, overrides, tokens, and cached config, then return to provisioning mode. |
| `DECOMMISSION` | Yes | Yes | UX accelerator only; backend auth/middleware remains the hard enforcement layer. |

### 4.2 Command Statuses

| Value | Meaning |
|---|---|
| `PENDING` | Command exists and is eligible for polling. |
| `DELIVERY_HINT_SENT` | Android push hint was attempted; polling is still required. |
| `ACKED` | Agent reported successful handling. |
| `FAILED` | Agent reported terminal failure. |
| `EXPIRED` | Command aged out before terminal handling. |
| `CANCELLED` | Operator/system cancelled before handling. |

### 4.3 Ack Semantics
- Ack request completion values are limited to `ACKED` and `FAILED`.
- `FAILED` is still an acknowledgement of command handling; it is not a transport failure.
- `failureCode` and `failureMessage` are populated only when `completionStatus = FAILED`.
- `result` is an optional non-sensitive execution summary. It is never a dump of local state.

### 4.4 Audit Event Names
- `BOOTSTRAP_TOKEN_USED`
- `AGENT_COMMAND_CREATED`
- `AGENT_COMMAND_ACKED`
- `AGENT_COMMAND_FAILED`
- `AGENT_COMMAND_EXPIRED`
- `AGENT_COMMAND_CANCELLED`
- `AGENT_PUSH_HINT_SENT`
- `AGENT_PUSH_HINT_FAILED`
- `AGENT_INSTALLATION_UPDATED`

These names are frozen in `src/cloud/FccMiddleware.Contracts/AgentControl/AgentControlAuditEventTypes.cs`.

## 5. DTO Definitions

### 5.1 Bootstrap Token History Row
- Contract: `BootstrapTokenHistoryRow`
- Purpose: render portal history without replaying audit events
- Required fields:
  - `tokenId`
  - `legalEntityId`
  - `siteCode`
  - `storedStatus`
  - `effectiveStatus`
  - `createdAt`
  - `expiresAt`
- Optional lifecycle fields:
  - `usedAt`
  - `usedByDeviceId`
  - `revokedAt`
  - `createdByActorId`
  - `createdByActorDisplay`
  - `revokedByActorId`
  - `revokedByActorDisplay`
- Security rule: raw bootstrap tokens and token hashes are never returned after creation.

### 5.2 Agent Command Create Request / Response
- Contracts:
  - `CreateAgentCommandRequest`
  - `CreateAgentCommandResponse`
- Request rules:
  - `commandType` is required and must be one of the three frozen command types.
  - `reason` is required and human-readable.
  - `payload` is optional JSON metadata and must remain non-sensitive.
  - `expiresAt` is optional; server default TTL applies when omitted.
- Response rules:
  - `commandId` is the global immutable idempotency key for later polls and acks.
  - response echoes `deviceId`, `legalEntityId`, `siteCode`, `commandType`, `status`, `reason`, and actor display fields.

### 5.3 Edge Command Poll Response
- Contract: `EdgeCommandPollResponse`
- Agent-facing fields:
  - `serverTimeUtc`
  - `commands[]`
- Each command item contains:
  - `commandId`
  - `commandType`
  - `status`
  - `reason`
  - `payload`
  - `createdAt`
  - `expiresAt`
- Polling rule: commands may be returned repeatedly until they transition to `ACKED`, `FAILED`, `EXPIRED`, or `CANCELLED`.

### 5.4 Command Ack Request / Response
- Contracts:
  - `CommandAckRequest`
  - `CommandAckResponse`
- Request fields:
  - `completionStatus`
  - `handledAtUtc`
  - `failureCode`
  - `failureMessage`
  - `result`
- Response fields:
  - `commandId`
  - `status`
  - `acknowledgedAt`
  - `duplicate`

### 5.5 Android Installation Token Upsert Request
- Contract: `AndroidInstallationUpsertRequest`
- Required fields:
  - `installationId`
  - `registrationToken`
  - `appVersion`
  - `osVersion`
  - `deviceModel`
- Security rule: `registrationToken` is marked sensitive and must never appear in structured logs or audit payloads.

### 5.6 Android Push Hint Payload
- Agent model constants:
  - `command_pending`
  - `config_changed`
- Payload is deliberately minimal:
  - `kind`
  - `deviceId`
  - `commandCount` or `configVersion`
- No secrets, config documents, command payloads, or executable content are sent over FCM.

## 6. Redaction Rules
- Never log or persist in audit payloads:
  - bootstrap tokens
  - bootstrap token hashes
  - FCM registration tokens
  - device JWTs
  - refresh tokens
  - passwords
  - API keys
  - customer tax IDs or equivalent PII
- `registrationToken` in `AndroidInstallationUpsertRequest` is annotated with `[Sensitive]` on cloud and `@Sensitive` on Android.
- `payload` and `result` JSON blocks on command DTOs must contain only operational metadata such as reason strings, booleans, counts, or version hints.
- If a command handler needs sensitive local context, it must derive and keep that context locally; it must not round-trip it into cloud command/audit JSON.

## 7. Idempotency Rules
- `commandId` is globally unique and assigned by cloud at creation time.
- Agent acks are idempotent:
  - replaying the same terminal outcome returns `duplicate = true`
  - replaying a conflicting terminal outcome is a state conflict and should be rejected
- Duplicate Android push hints are acceptable; hints are acceleration only.
- Installation upsert is keyed by authenticated device identity plus `installationId`; clients may safely resend the latest token.

## 8. Downstream Implementation Impact
- Cloud `Phase 1` can add controllers, handlers, EF models, and migrations without reopening DTO semantics.
- Android `Phase 2` can implement command fetch/ack and FCM registration directly against the frozen shapes already mirrored in `CloudApiModels.kt`.
- Desktop `Phase 2` can implement polling and acks against the same vocabulary already mirrored in `AgentControlModels.cs`.

## 9. Open Questions
- None for DTO semantics.
- FCM rollout posture remains gated by physical Urovo validation and is documented separately in `docs/specs/testing/tier-1-6-urovo-fcm-viability-spike.md`.
