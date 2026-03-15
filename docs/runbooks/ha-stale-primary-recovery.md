# Runbook: HA Stale Primary Recovery

## When to Use

A stale primary exists when an agent believes it is the primary but the cloud and/or peers have moved to a higher epoch. This typically happens after:
- Network partition healed (the agent was isolated)
- Agent restarted after an automatic failover occurred
- Manual epoch increment while the agent was offline

## Symptoms

- Cloud returns `CONFLICT.STALE_LEADER_EPOCH` or `CONFLICT.NON_LEADER_WRITE` on uploads
- Agent telemetry shows `currentRole: PRIMARY` but portal shows a different leader
- Audit events show `HA_STALE_WRITER_REJECTED`

## Recovery Procedure

### 1. Identify the Stale Agent

```
GET /api/v1/agents?siteCode={SITE_CODE}&legalEntityId={LEGAL_ENTITY_ID}
```

Look for an agent where:
- `currentRole: PRIMARY` but `isCurrentLeader: false`
- `leaderEpochSeen` < the site's current `leaderEpoch`

### 2. Automatic Recovery (Preferred)

In most cases, the stale agent will self-recover:
1. On the next heartbeat exchange, it observes the higher epoch from a peer
2. `OnEpochObservedAsync` triggers `SELF_DEMOTION`
3. Agent transitions to `RECOVERING` → replicates from new primary → `STANDBY_HOT`

Monitor events for `HA_PRIMARY_SELF_DEMOTED` and `HA_RECOVERY_COMPLETE`.

### 3. Manual Recovery (If Automatic Fails)

If the agent cannot reach peers (e.g., persistent network issue):

```
POST /api/v1/admin/agents/{STALE_DEVICE_ID}/commands
{
  "commandType": "FORCE_CONFIG_PULL",
  "reason": "Force stale primary to re-sync config and observe current epoch"
}
```

The config pull will deliver the current `leaderEpoch` and `peerDirectory`, triggering self-demotion.

### 4. Last Resort: Reset Local State

If the agent is in a corrupted state:

```
POST /api/v1/admin/agents/{STALE_DEVICE_ID}/commands
{
  "commandType": "RESET_LOCAL_STATE",
  "reason": "Reset stale primary after failed recovery"
}
```

This clears the local database and forces a full re-bootstrap from the new primary.

## Prevention

- Ensure `heartbeatIntervalSeconds` and `failoverTimeoutSeconds` are tuned for your network
- Monitor `HA_STALE_WRITER_REJECTED` audit events for early detection
- Keep agents on the same app version to avoid protocol mismatches
