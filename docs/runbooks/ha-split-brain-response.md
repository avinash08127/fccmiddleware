# Runbook: HA Split Brain Response

## What Is Split Brain

Split brain occurs when two agents simultaneously believe they are the primary for the same site. This can happen during:
- Network partition where both sides perform independent elections
- Race condition during failover (very unlikely with epoch-based fencing)
- Bug in election coordination

## Impact

- Cloud write fencing prevents data corruption: only the agent with the correct epoch can write
- The stale primary's uploads will be rejected with `CONFLICT.STALE_LEADER_EPOCH`
- Odoo POS clients connected to the stale primary will still receive responses (from stale cache)

## Detection

### Automated Detection

- Cloud logs `HA_STALE_WRITER_REJECTED` audit events
- Telemetry shows two agents with `currentRole: PRIMARY` for the same site
- Portal agent list shows `isCurrentLeader: false` for one of the primaries

### Manual Detection

```
GET /api/v1/agents?siteCode={SITE_CODE}&legalEntityId={LEGAL_ENTITY_ID}
```

If two agents show `currentRole: PRIMARY`, split brain is confirmed.

## Resolution

### Step 1: Identify the Legitimate Primary

The legitimate primary is the one with the higher `leaderEpoch`. Check:
- `leaderEpochSeen` on both agents
- The cloud's resolved leadership via the portal

### Step 2: Wait for Self-Resolution (2 Minutes)

In most cases, split brain resolves automatically:
1. Heartbeat exchange delivers the higher epoch to the stale primary
2. `OnEpochObservedAsync` triggers immediate `SELF_DEMOTION`
3. Stale primary transitions to `RECOVERING` → `STANDBY_HOT`

### Step 3: Force Resolution (If Automatic Fails)

If the stale primary cannot reach the legitimate primary (network partition persists):

```
POST /api/v1/admin/agents/{STALE_PRIMARY_DEVICE_ID}/commands
{
  "commandType": "FORCE_CONFIG_PULL",
  "reason": "Force epoch update to resolve split brain"
}
```

The config pull delivers the current epoch and peer directory. The agent observes the higher epoch and self-demotes.

### Step 4: Verify Resolution

```
GET /api/v1/agents?siteCode={SITE_CODE}&legalEntityId={LEGAL_ENTITY_ID}
```

Confirm:
- Exactly one agent shows `currentRole: PRIMARY`, `isCurrentLeader: true`
- The former stale primary shows `currentRole: STANDBY_HOT` or `RECOVERING`

### Step 5: Reconcile Any Missed Data

If the stale primary accepted transactions from Odoo during the split:
- Those transactions were buffered locally but rejected by cloud
- Once the stale primary becomes a standby and replicates from the new primary, its local buffer will be reconciled
- Check for `DEAD_LETTER` records that may need manual replay

## Prevention

- Ensure `failoverTimeoutSeconds` is long enough for transient network blips (default: 30s)
- Keep `heartbeatIntervalSeconds` short (default: 5s) for fast detection
- The priority-based election backoff (`priority * 200ms + random(0-500ms)`) naturally prevents simultaneous elections
- Monitor `HA_PEER_SUSPECTED` events for early warning of connectivity issues
