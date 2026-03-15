# Runbook: HA Forced Switchover

## When to Use

Use this runbook when you need to manually promote a standby agent to primary, typically for:
- Planned maintenance on the current primary device
- Hardware replacement of the primary
- Testing failover behavior in a controlled manner

## Prerequisites

- Portal admin access (PortalAdminWrite role)
- Target standby agent must be in `STANDBY_HOT` state with `HOT` readiness
- Replication lag must be within `maxReplicationLagSeconds` threshold (default: 15s)

## Procedure

### 1. Verify Standby Readiness

```
GET /api/v1/agents?siteCode={SITE_CODE}&legalEntityId={LEGAL_ENTITY_ID}
```

Confirm the target agent shows:
- `currentRole: STANDBY_HOT`
- `lastReplicationLagSeconds` <= threshold
- `status: ACTIVE`

### 2. Initiate Switchover

```
POST /api/v1/agents/switchover
{
  "siteCode": "SITE_CODE",
  "targetAgentId": "TARGET_AGENT_UUID",
  "reason": "Planned maintenance on primary device"
}
```

Returns `commandId` for tracking.

### 3. Monitor Progress

Watch the agent events endpoint for the current primary:
```
GET /api/v1/agents/{PRIMARY_DEVICE_ID}/events?limit=10
```

Expected event sequence:
1. `HA_SWITCHOVER_STARTED` — command received by primary
2. `HA_PRIMARY_SELF_DEMOTED` — primary drains in-flight, flushes replication
3. `HA_PRIMARY_ELECTED` — target assumes leadership with incremented epoch
4. `HA_SWITCHOVER_COMPLETED` — switchover confirmed

### 4. Verify Completion

```
GET /api/v1/agents?siteCode={SITE_CODE}&legalEntityId={LEGAL_ENTITY_ID}
```

Confirm:
- New primary shows `currentRole: PRIMARY`, `isCurrentLeader: true`
- Old primary shows `currentRole: STANDBY_HOT`
- Epoch has incremented

## Troubleshooting

### Switchover Stuck

If the switchover command remains `PENDING` after 2 minutes:
1. Check primary agent connectivity (telemetry endpoint)
2. If primary is unreachable, consider automatic failover (if enabled) or manual epoch increment

### Target Not Ready

If switchover returns `CONFLICT.STANDBY_NOT_READY`:
1. Check replication lag on the target agent
2. Wait for lag to decrease below threshold
3. If lag persists, check network connectivity between agents

### Split Brain After Switchover

If both agents report `PRIMARY`:
1. The epoch-based fencing will reject writes from the stale primary
2. The stale primary should self-demote upon observing the higher epoch
3. If it doesn't, send a `FORCE_CONFIG_PULL` command to the stale primary
