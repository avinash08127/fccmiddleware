# Operational Runbook: Agent Control Phase 5

Operational procedures for suspicious-device review, token history investigation, reset/decommission recovery, and FCM hardening.

---

## Token History Investigation

Use this when a site reports failed provisioning, duplicate registration attempts, or an unexpected device replacement.

1. Query bootstrap-token history for the site and legal entity.
   - Confirm token `status`, `createdByActorDisplay`, `revokedAt`, `usedAt`, and `usedByDeviceId`.
2. Compare the token history timeline with the agent registration row.
   - Check `status`, `deviceSerialNumber`, `approvalGrantedAt`, `suspensionReasonCode`, and `replacementForDeviceId`.
3. Review audit events for the device and site.
   - Look for `BOOTSTRAP_TOKEN_USED`, `SUSPICIOUS_REGISTRATION_HELD`, `SUSPICIOUS_REGISTRATION_APPROVED`, `SUSPICIOUS_REGISTRATION_REJECTED`, and `DEVICE_DECOMMISSIONED`.
4. Classify the outcome.
   - `PENDING_APPROVAL`: operator review still needed.
   - `QUARANTINED`: a security-rule mismatch triggered the hold.
   - `DEACTIVATED`: the request was rejected or the device was decommissioned.
5. Close the investigation with one action.
   - Approve the suspicious registration and ask the technician to retry provisioning.
   - Reject or decommission the registration and issue a new bootstrap token only after the root cause is understood.

## Reset Command Recovery

Use this when `RESET_LOCAL_STATE` was sent to the wrong agent, the device did not return to service, or an operator needs to recover after a successful reset.

1. Open the agent command history and confirm the command status.
   - `ACKED`: the device processed the reset.
   - `FAILED` or `EXPIRED`: the reset did not complete; retry only after checking connectivity.
2. If the reset succeeded, expect the device to return to provisioning mode.
   - The existing registration may remain `ACTIVE` until the replacement flow completes.
3. Generate a fresh bootstrap token if the original token has already been consumed or expired.
4. If the replacement device is intentionally different, watch for phase-5 holds.
   - `PENDING_APPROVAL` requires portal approval before the reprovision attempt can finish.
5. If the wrong device was reset, document the actor, reason, and recovery token in the site’s operational ticket.

## Decommission Recovery

Use this when a device was decommissioned accidentally or the wrong device at a site was removed from service.

1. Confirm the decommission event in audit history.
   - Capture `DecommissionedBy`, `Reason`, `DeactivatedAt`, and any cancelled commands.
2. Verify whether a replacement device already exists.
   - If a suspicious replacement row exists, either approve it or reject it before generating another token.
3. Issue a new bootstrap token.
   - Decommission is terminal for the old token pair; do not try to reactivate the old refresh token.
4. Re-provision the intended device.
   - If the reprovision attempt is held in `PENDING_APPROVAL` or `QUARANTINED`, complete the review path before retrying.
5. Close the incident by linking the replacement registration row and the decommission audit event in the incident record.

## FCM Token Churn

Use this when Android installations frequently rotate registration tokens, push hints start failing, or command acceleration becomes inconsistent.

1. Check the latest `agent_installations` row for the device.
   - Compare `last_seen_at`, `last_hint_sent_at`, app version, OS version, and token-hash churn over time.
2. Review push-hint audit events.
   - `AGENT_PUSH_HINT_SENT` confirms dispatch.
   - `AGENT_PUSH_HINT_FAILED` indicates auth, HTTP, or stale-token failures.
3. If churn is isolated to one device:
   - Force the Android client to re-register its installation token on next heartbeat/config poll.
   - Confirm the device still reaches the command poller over HTTPS.
4. If churn is broad:
   - Treat it as an FCM provider issue or Android build regression.
   - Disable `AgentCommands:FcmHintsEnabled` and rely on pollers until stability returns.

## FCM Outage Behavior

FCM is an acceleration path only. Correctness must remain intact when push delivery is unavailable.

1. Disable `AgentCommands:FcmHintsEnabled` if push failures are sustained or noisy.
2. Leave `AgentCommands:Enabled` on if command polling is healthy.
3. Verify the fallback path.
   - Android and desktop agents must still fetch commands/config on their normal HTTPS cadence.
4. Monitor the impact.
   - Expect slower command/config pickup, not functional data loss.
5. Re-enable FCM hints only after:
   - Firebase credentials are valid,
   - push-hint failures have cleared,
   - canary devices confirm normal wake-up behavior.
