# Rollout Runbook: Agent Control Phase 5

Rollout plan for suspicious-device workflow, token-history operations, and FCM hardening.

---

## Environment Configuration

Configure these values before enabling phase 5 in production:

- `BootstrapTokens:HistoryApiEnabled`
  - Enables the token-history API and portal history screen.
- `AgentCommands:Enabled`
  - Enables command polling and admin command APIs.
- `AgentCommands:FcmHintsEnabled`
  - Enables FCM wake-up hints on top of command polling.
- `SuspiciousDeviceWorkflow:Enabled`
  - Master flag for approval-gated onboarding. Keep `false` for MVP rollout.
- `SuspiciousDeviceWorkflow:HoldUnexpectedSerialReplacement`
- `SuspiciousDeviceWorkflow:HoldSiteOccupancyWithoutApproval`
- `SuspiciousDeviceWorkflow:QuarantineSecurityRuleMismatch`
- `SuspiciousDeviceWorkflow:MinimumAgentVersion`
- `SuspiciousDeviceWorkflow:AllowedDeviceModels`
- `SuspiciousDeviceWorkflow:AllowedSerialPrefixes`
- `Firebase:Messaging:ProjectId`
- `Firebase:Messaging:ClientEmail`
- `Firebase:Messaging:PrivateKey`

## Secret Management

Do not store Firebase service-account secrets in source control.

1. Load `Firebase:Messaging:ClientEmail` and `Firebase:Messaging:PrivateKey` from the deployment secret store.
2. Prefer environment-variable or secret-provider overrides over checked-in `appsettings`.
3. Rotate the Firebase private key if:
   - an environment was misconfigured,
   - the service account was exposed,
   - FCM auth failures suggest credential drift.

## Rollout Order

Apply phase 5 incrementally in this order:

1. Backend schema and APIs
   - Apply `db/migrations/010-agent-control-phase5.sql`.
   - Deploy the backend with phase-5 endpoints and the new registration-state model.
   - Keep `SuspiciousDeviceWorkflow:Enabled=false`.
2. Portal token history and suspicious-device actions
   - Deploy the portal once backend contracts are live.
   - Verify token history, held-state badges, and approve/reject/decommission actions.
3. Agent pollers
   - Confirm Android and desktop agents still behave correctly with HTTPS polling only.
   - Validate desktop registration messaging for `DEVICE_PENDING_APPROVAL` and `DEVICE_QUARANTINED`.
4. FCM acceleration
   - Enable `AgentCommands:FcmHintsEnabled` only after canary validation on real hardware.

## Canary Plan

Use a controlled rollout before broad enablement:

1. Select 1-2 legal entities and a small set of sites.
2. Enable backend and portal changes with `SuspiciousDeviceWorkflow:Enabled=false`.
3. Validate normal provisioning, token history, command polling, and decommission recovery.
4. Turn on `SuspiciousDeviceWorkflow:Enabled=true` for the canary environment only.
5. Exercise the three intended breach types:
   - unexpected serial replacement,
   - occupied site without approval,
   - security-rule mismatch.
6. Confirm that:
   - the device is held server-side,
   - the portal can approve/reject/decommission the row,
   - an approved retry activates successfully,
   - rejected/decommissioned rows stay inactive.

## Rollback

If the rollout creates operational friction or false positives:

1. Set `SuspiciousDeviceWorkflow:Enabled=false`.
   - New registrations return to the existing fast path.
2. Leave the schema in place.
   - The added columns are backward-compatible and do not require immediate rollback.
3. If FCM is unstable, set `AgentCommands:FcmHintsEnabled=false`.
4. If portal actions misbehave, roll back the portal deployment while leaving backend flags off.
5. If backend API behavior regresses, roll back the backend deployment after confirming no in-flight migration failure.

## Post-Rollout Checks

After each rollout stage, verify:

- registration success rate by site,
- count of `PENDING_APPROVAL` and `QUARANTINED` rows,
- approve/reject turnaround time,
- bootstrap-token history query latency,
- FCM push-hint success/failure ratio,
- command pickup latency with and without FCM hints.
