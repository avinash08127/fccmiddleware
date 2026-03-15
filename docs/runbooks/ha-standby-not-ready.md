# Runbook: HA Standby Not Ready

## When to Use

A standby agent reports readiness state `CATCHING_UP` or `BLOCKED` instead of `HOT`. This means it cannot be promoted to primary (neither via planned switchover nor automatic failover).

## Diagnosis

### 1. Check Current Readiness

```
GET /api/v1/agents?siteCode={SITE_CODE}&legalEntityId={LEGAL_ENTITY_ID}
```

Look at `lastReplicationLagSeconds` for the standby agent. If null, replication may not be running.

### 2. Determine Readiness State

The readiness gate evaluates these conditions:

**BLOCKED** (most severe):
- `snapshotComplete = false` — initial bootstrap not finished
- `configVersion` mismatch between primary and standby
- `primaryEpoch = 0` — no primary connection established

**CATCHING_UP** (intermediate):
- Time since last delta sync > `maxReplicationLagSeconds`
- Replication sequence gap > 100 records

**HOT** (ready for promotion):
- All other conditions pass

## Resolution by Root Cause

### Snapshot Not Complete

The standby needs to complete its initial bootstrap from the primary.

1. Verify the primary is reachable from the standby (same LAN, peer API port open)
2. Check standby logs for bootstrap errors
3. If the primary's peer API is unreachable, check firewall rules for port `peerApiPort` (default 8586)
4. If bootstrap keeps failing, send `FORCE_CONFIG_PULL` to the standby to re-read the peer directory

### Config Version Mismatch

The standby has a different config version than the primary.

1. Send `FORCE_CONFIG_PULL` to both agents
2. Wait for both to apply the same config version
3. Readiness should resolve automatically after the next sync cycle

### High Replication Lag

The standby is falling behind the primary's write rate.

1. Check network throughput between agents (peer API is HTTP over LAN)
2. Check standby's CPU/disk load — it may not be processing syncs fast enough
3. Temporarily reduce the primary's write rate if possible
4. Check if `replicationEnabled` flag is true in the config

### Primary Not Reachable

1. Verify both agents are on the same LAN subnet
2. Check that port `peerApiPort` (default 8586) is not blocked by firewall
3. Verify the primary's `peerApiBaseUrl` in the peer directory is correct
4. Check HMAC shared secret matches between agents (config `peerSharedSecret`)

## Monitoring

Set up alerts on `lastReplicationLagSeconds` exceeding `maxReplicationLagSeconds` for more than 5 minutes. The `HA_REPLICATION_LAG_EXCEEDED` audit event fires when this threshold is crossed.
