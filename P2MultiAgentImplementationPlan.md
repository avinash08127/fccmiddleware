# P2 — Multi-Agent Enhancement Implementation Plan

**Status:** Ready for Implementation
**Date:** 2026-03-15
**Source:** Section 19 (New Enhancements) of MultiAgents.md
**Scope:** Concrete implementation tasks for all gaps, design improvements, and config sync enhancements identified during the multi-agent architecture review.

---

## Task Index

| ID | Task | Category | Phase | Priority |
|---|---|---|---|---|
| P2-01 | Add CANDIDATE runtime state | State Model | 0 | P0 |
| P2-02 | Update plan text for Android-only topology | Documentation | 0 | P0 |
| P2-03 | Add peerDirectoryVersion to cloud site model | Config Sync | 1 | P0 |
| P2-04 | Emit X-Peer-Directory-Version header on all agent-facing responses | Config Sync | 1 | P0 |
| P2-05 | Increment peerDirectoryVersion on registration and status change | Config Sync | 1 | P0 |
| P2-06 | Add REFRESH_CONFIG agent command type | Config Sync | 1 | P0 |
| P2-07 | Enqueue REFRESH_CONFIG on peer-directory-affecting events | Config Sync | 1 | P0 |
| P2-08 | Android — read X-Peer-Directory-Version and trigger config refresh | Config Sync | 1 | P0 |
| P2-09 | Desktop — read X-Peer-Directory-Version and trigger config refresh | Config Sync | 1 | P0 |
| P2-10 | Reduce Android config poll interval for HA-enabled sites | Config Sync | 1 | P1 |
| P2-11 | Reduce cloud config cache TTL for HA-enabled sites | Config Sync | 1 | P1 |
| P2-12 | LAN UDP peer announcement — broadcast on startup | Config Sync | 2 | P1 |
| P2-13 | LAN UDP peer announcement — listener | Config Sync | 2 | P1 |
| P2-14 | Define election algorithm explicitly | Design | 0 | P0 |
| P2-15 | Define cloud role during elections (Option B) | Design | 0–1 | P0 |
| P2-16 | FCC session handover research per vendor | Research | 0 | P0 |
| P2-17 | Define in-flight pre-auth handling during failover | Design | 0 | P0 |
| P2-18 | Document ingestion mode × failover interaction | Design | 0 | P1 |
| P2-19 | Define clock synchronization approach | Design | 2 | P1 |
| P2-20 | Move localhost facade contract definition to Phase 2 | Design | 2 | P1 |
| P2-21 | Resolve READ_ONLY runtime role | State Model | 0 | P2 |
| P2-22 | Add rollback and degradation plan | Operations | 1 | P0 |
| P2-23 | Add failover alerting rules | Operations | 1 | P1 |
| P2-24 | Define cold-start election behavior | Design | 1 | P1 |
| P2-25 | Map all SiteHa config fields to desktop AgentConfiguration | Desktop Agent | 1 | P1 |
| P2-26 | Android background execution verification on Urovo i9100 | Research | 2 | P0 |
| P2-27 | Add FCC vendor takeover test scenarios | Testing | 2 | P1 |
| P2-28 | Add peerDirectoryVersion to test factories and contract tests | Testing | 1 | P1 |

---

## P2-01: Add CANDIDATE Runtime State

**Category:** State Model
**Phase:** 0
**Priority:** P0
**Depends on:** None

### Problem

MA-0.1 instruction #4 asks for a state diagram covering `PRIMARY -> STANDBY_HOT -> CANDIDATE -> RECOVERING`, but CANDIDATE is not defined in the runtime roles (§4.1 of MultiAgents.md). The config schema (`edge-agent-config.schema.json`) and site config schema (`site-config.schema.json`) do not include CANDIDATE as a valid role value.

### What to Change

**1. MultiAgents.md — section 4.1 Runtime roles**

Add between STANDBY_HOT and RECOVERING:

```
- `CANDIDATE`
  - has detected primary failure and is attempting to claim leadership
  - cannot serve authoritative writes or accept FCC commands
  - broadcasts a signed leadership claim with incremented epoch
  - exits to PRIMARY on successful claim, or STANDBY_HOT if a higher-priority claim is observed or timeout expires
  - multiple simultaneous CANDIDATEs are allowed; at most one transitions to PRIMARY
```

**2. schemas/config/edge-agent-config.schema.json**

File: `schemas/config/edge-agent-config.schema.json`

Add `CANDIDATE` to the `roleCapability` enum (this is a config-level field, so it may not need CANDIDATE — CANDIDATE is a runtime-only state). Verify the distinction between config-level roles and runtime-level roles.

**3. schemas/config/site-config.schema.json**

File: `schemas/config/site-config.schema.json`

Add `CANDIDATE` to the `currentRole` enum in the peer directory entry schema. The cloud needs to display CANDIDATE when an agent is mid-election.

**4. Cloud — SiteHaLeadershipResolver.cs**

File: `src/cloud/FccMiddleware.Application/AgentConfig/SiteHaLeadershipResolver.cs`

Method `ResolveCurrentRole()` — add logic: if an agent has reported itself as CANDIDATE, reflect that in the peer directory rather than overriding with computed role.

**5. Desktop — SiteConfig.cs**

File: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/SiteConfig.cs`
Line 162: `CurrentRole` property — ensure CANDIDATE is a valid value.

**6. Android — CloudApiModels.kt / EdgeAgentConfigDto**

Add CANDIDATE to any role enum or validation logic in the config DTOs.

### Acceptance Criteria

- [ ] CANDIDATE is documented in MultiAgents.md §4.1 with entry/exit conditions
- [ ] State diagram shows all transitions: STANDBY_HOT → CANDIDATE → PRIMARY, CANDIDATE → STANDBY_HOT (timeout/higher claim)
- [ ] `site-config.schema.json` `currentRole` enum includes CANDIDATE
- [ ] Cloud peer directory can display CANDIDATE for an agent mid-election
- [ ] No existing code breaks when CANDIDATE appears as a role value

---

## P2-02: Update Plan Text for Android-Only Topology

**Category:** Documentation
**Phase:** 0
**Priority:** P0
**Depends on:** None

### Problem

The plan text in MultiAgents.md §1 says "one Desktop Edge Agent and one or more Android Edge Agents can run in parallel." A single Android agent running alone (no desktop) is a confirmed valid production topology.

### What to Change

**1. MultiAgents.md — section 1 (Objective)**

Replace:
> one Desktop Edge Agent and one or more Android Edge Agents can run in parallel

With:
> one or more Edge Agents (Android and/or Desktop) can run in parallel at a site

**2. MultiAgents.md — section 4.2 (Primary selection policy)**

Replace:
> Default site priority order:
> 1. Desktop agent
> 2. Highest-priority Android agent
> 3. Next Android agents by configured priority

With:
> Default site priority order (configuration-driven per site):
> 1. Desktop agent (if present)
> 2. Android agents by configured priority
>
> Valid topologies:
> - 1 Android agent (no HA — trivially PRIMARY)
> - 2+ Android agents, no desktop (full HA between Android peers)
> - 1 Desktop + 1+ Android agents (desktop-preferred PRIMARY)
> - 1 Desktop agent only (no HA — trivially PRIMARY)

**3. MultiAgents.md — section 6 (Phase Overview)**

Add a note: "All phases must work for Android-only sites. Desktop is optional."

### Acceptance Criteria

- [ ] §1 no longer implies desktop is required
- [ ] §4.2 lists all four valid topologies explicitly
- [ ] Android-only topology is referenced as the first example (most common)

---

## P2-03: Add peerDirectoryVersion to Cloud Site Model

**Category:** Config Sync — Layer 1
**Phase:** 1
**Priority:** P0
**Depends on:** None

### Problem

No mechanism exists to notify agents when the peer directory changes. The cloud needs a monotonically increasing version counter per site.

### Files to Modify

**1. Site entity**

File: `src/cloud/FccMiddleware.Domain/Entities/Site.cs`
Lines: 10-41

Add property:
```csharp
public long PeerDirectoryVersion { get; set; }
```

Default: 0. Incremented on any peer-directory-affecting event.

**2. Database migration**

Add EF Core migration to add `PeerDirectoryVersion` column (bigint, default 0) to the Sites table.

**3. IRegistrationDbContext**

File: `src/cloud/FccMiddleware.Application/Registration/IRegistrationDbContext.cs`

Ensure `FindSiteBySiteCodeAsync()` returns the Site entity with `PeerDirectoryVersion` accessible.

### Acceptance Criteria

- [ ] Site entity has `PeerDirectoryVersion` property
- [ ] Migration adds column with default 0
- [ ] Existing sites get PeerDirectoryVersion = 0 on migration
- [ ] Column is queryable in EF Core

---

## P2-04: Emit X-Peer-Directory-Version Header on All Agent-Facing Responses

**Category:** Config Sync — Layer 1
**Phase:** 1
**Priority:** P0
**Depends on:** P2-03

### Problem

Agents need to detect peer directory changes on every cloud contact, not just on config polls.

### Files to Modify

**1. New middleware: PeerDirectoryVersionMiddleware**

Create: `src/cloud/FccMiddleware.Api/Infrastructure/PeerDirectoryVersionMiddleware.cs`

This middleware runs on all agent-facing API requests (identified by the device JWT auth scheme). It:
- Extracts `deviceId` from the authenticated claims
- Looks up the agent's site
- Reads `site.PeerDirectoryVersion`
- Sets `Response.Headers["X-Peer-Directory-Version"] = version.ToString()` before the response is sent

Implementation note: cache the site lookup per request (the device-active-check middleware already resolves the device — reuse that context).

**2. Register middleware in Program.cs**

File: `src/cloud/FccMiddleware.Api/Program.cs`
After line 586 (`DeviceActiveCheckMiddleware`) — the device is already resolved at this point.

Add:
```csharp
app.UsePeerDirectoryVersionHeader();
```

Only activate for agent-facing routes (device JWT scheme), not portal routes.

**3. Agent-facing endpoints that will carry the header**

All of these will automatically get the header via middleware:
- `GET /api/v1/agent/config` (AgentController)
- `POST /api/v1/transactions/upload` (TransactionsController)
- `POST /api/v1/preauth` (PreAuthController)
- `GET /api/v1/agent/commands` (AgentController)
- `POST /api/v1/agent/telemetry` (AgentController)

### Acceptance Criteria

- [ ] Every HTTP response to an authenticated agent includes `X-Peer-Directory-Version` header
- [ ] Header value matches the current `Site.PeerDirectoryVersion` for the agent's site
- [ ] Portal (non-agent) responses do NOT include the header
- [ ] Header is present on 200, 201, 304, and 409 responses (all status codes)
- [ ] Unit test: mock site with PeerDirectoryVersion=5, verify header value is "5"

---

## P2-05: Increment peerDirectoryVersion on Registration and Status Change

**Category:** Config Sync — Layer 1
**Phase:** 1
**Priority:** P0
**Depends on:** P2-03

### Files to Modify

**1. RegisterDeviceHandler.cs**

File: `src/cloud/FccMiddleware.Application/Registration/RegisterDeviceHandler.cs`
Method: `Handle()`, after successful save at line 283-292.

Add before `TrySaveChangesAsync()`:
```csharp
site.PeerDirectoryVersion++;
```

This ensures the version increments atomically with the registration save.

**2. Agent deactivation/decommission paths**

Anywhere an agent's status changes (deactivation, decommission, heartbeat-stale detection), increment the site's `PeerDirectoryVersion`. Identify all paths:

- `RegisterDeviceHandler.cs` line 146-154: `replacePreviousAgent` deactivation — add `site.PeerDirectoryVersion++`
- Any future agent status update endpoint (e.g., heartbeat staleness worker) — add increment
- `AgentsController.cs` `PlannedSwitchover()` line 735-755: role change — add increment

**3. Cloud-side stale heartbeat detection (future)**

When implemented, any background job that marks an agent as OFFLINE due to stale heartbeat must also increment `PeerDirectoryVersion`.

### Acceptance Criteria

- [ ] New agent registration increments `PeerDirectoryVersion` for the site
- [ ] Agent deactivation (replacement) increments `PeerDirectoryVersion`
- [ ] Planned switchover increments `PeerDirectoryVersion`
- [ ] Version is incremented within the same DB transaction as the state change
- [ ] Integration test: register agent, verify `PeerDirectoryVersion` increased by 1

---

## P2-06: Add REFRESH_CONFIG Agent Command Type

**Category:** Config Sync — Layer 2
**Phase:** 1
**Priority:** P0
**Depends on:** None

### Files to Modify

**1. AgentCommandType enum (cloud)**

File: `src/cloud/FccMiddleware.Domain/Enums/AgentCommandType.cs`

Add:
```csharp
REFRESH_CONFIG
```

This is distinct from `FORCE_CONFIG_PULL` (which already exists). `REFRESH_CONFIG` is system-generated (enqueued by the cloud on peer directory changes), while `FORCE_CONFIG_PULL` is operator-triggered.

**2. Android — AgentCommandType (Kotlin)**

File: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiModels.kt`
Around line 389-394.

Add `REFRESH_CONFIG` to the enum.

**3. Android — AgentCommandExecutor.kt**

File: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/AgentCommandExecutor.kt`

Add handler case:
```kotlin
AgentCommandType.REFRESH_CONFIG -> executeRefreshConfig(command)
```

The `executeRefreshConfig` method should call `cadenceController.triggerImmediateConfigPoll()` (line 390-402 in CadenceController.kt).

**4. Desktop — CommandPollWorker.cs or command executor**

File: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CommandPollWorker.cs`

Add handler for `REFRESH_CONFIG` command that triggers immediate config poll via the cadence controller.

### Acceptance Criteria

- [ ] `REFRESH_CONFIG` is a valid AgentCommandType in cloud, Android, and desktop
- [ ] Android agent receiving `REFRESH_CONFIG` triggers immediate config poll
- [ ] Desktop agent receiving `REFRESH_CONFIG` triggers immediate config poll
- [ ] Command is acknowledged to cloud after execution
- [ ] Unit test: simulate REFRESH_CONFIG command, verify config poll is triggered

---

## P2-07: Enqueue REFRESH_CONFIG on Peer-Directory-Affecting Events

**Category:** Config Sync — Layer 2
**Phase:** 1
**Priority:** P0
**Depends on:** P2-05, P2-06

### Files to Modify

**1. RegisterDeviceHandler.cs**

File: `src/cloud/FccMiddleware.Application/Registration/RegisterDeviceHandler.cs`
Method: `Handle()`, after `site.PeerDirectoryVersion++` (P2-05), before `TrySaveChangesAsync()`.

Add logic to create `AgentCommand` records for all **other** active agents at the same site:

```csharp
var otherAgents = await _db.FindActiveAgentsForSiteAsync(request.SiteCode, ct);
foreach (var peer in otherAgents.Where(a => a.DeviceId != newAgent.DeviceId))
{
    _db.AgentCommands.Add(new AgentCommand
    {
        DeviceId = peer.DeviceId,
        LegalEntityId = request.LegalEntityId,
        SiteCode = request.SiteCode,
        CommandType = AgentCommandType.REFRESH_CONFIG,
        Status = AgentCommandStatus.PENDING,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        CreatedAt = DateTimeOffset.UtcNow,
    });
}
```

This uses the existing `AgentCommand` entity and `AgentCommands` DbSet (line 72 in FccMiddlewareDbContext.cs). Commands expire after 10 minutes since they are advisory nudges, not critical operations.

**2. Agent deactivation paths**

Same pattern: when an agent is deactivated or decommissioned, enqueue `REFRESH_CONFIG` for remaining active agents at the site.

**3. PlannedSwitchover**

File: `src/cloud/FccMiddleware.Api/Controllers/AgentsController.cs`
Method: `PlannedSwitchover()` line 735-755.

After the switchover command is created, also enqueue `REFRESH_CONFIG` for all other agents at the site (they need to see the new leader in their peer directory).

### Acceptance Criteria

- [ ] New agent registration enqueues `REFRESH_CONFIG` for all other active agents at the same site
- [ ] Agent deactivation enqueues `REFRESH_CONFIG` for remaining agents
- [ ] Commands are created in the same DB transaction as the registration/status change
- [ ] Commands have a 10-minute expiry (advisory, not critical)
- [ ] Integration test: register second agent at site, verify REFRESH_CONFIG command exists for first agent

---

## P2-08: Android — Read X-Peer-Directory-Version and Trigger Config Refresh

**Category:** Config Sync — Layer 1 (agent side)
**Phase:** 1
**Priority:** P0
**Depends on:** P2-04

### Files to Modify

**1. CloudApiClient.kt — extract header from all responses**

File: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudApiClient.kt`

Add a shared helper method:
```kotlin
private fun extractPeerDirectoryVersion(response: HttpResponse): Long? =
    response.headers["X-Peer-Directory-Version"]?.toLongOrNull()
```

Call this in:
- `getConfig()` (line 489-531): pass version through `CloudConfigPollResult`
- `uploadTransactions()` (around line 400-440): return version alongside upload result
- `forwardPreAuth()` (around line 174): return version alongside forward result
- `pollCommands()`: return version alongside command result

**2. ConfigManager.kt — track local peerDirectoryVersion**

File: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/ConfigManager.kt`

Add:
```kotlin
private val _peerDirectoryVersion = AtomicLong(0L)
val currentPeerDirectoryVersion: Long get() = _peerDirectoryVersion.get()

fun updatePeerDirectoryVersion(version: Long) {
    _peerDirectoryVersion.set(version)
}

fun isPeerDirectoryStale(cloudVersion: Long): Boolean =
    cloudVersion > _peerDirectoryVersion.get()
```

On `applyConfig()`, extract `peerDirectoryVersion` from the SiteHa section (or from the stored header value) and update.

**3. CadenceController.kt — check version on every cloud response**

File: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt`

After any cloud worker returns a response, check:
```kotlin
if (peerDirectoryVersion != null && configManager.isPeerDirectoryStale(peerDirectoryVersion)) {
    triggerImmediateConfigPoll()  // existing method at line 390-402
}
```

The most practical integration point is in `runTick()` (line 505-641) after each worker completes. Workers already return result objects — extend them to carry the `peerDirectoryVersion` from the response header.

**4. Persist peerDirectoryVersion locally**

File: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/dao/SyncStateDao.kt`

Add column `peerDirectoryVersion` (Long, default 0) to the SyncState entity so it survives app restart.

### Acceptance Criteria

- [ ] Every cloud HTTP response header is checked for `X-Peer-Directory-Version`
- [ ] If cloud version > local version, immediate config poll is triggered
- [ ] Local peerDirectoryVersion is persisted in SyncState (survives restart)
- [ ] Config poll updates local peerDirectoryVersion from the config response
- [ ] Unit test: mock cloud response with X-Peer-Directory-Version=3, local=2 → config poll triggered
- [ ] Unit test: mock cloud response with X-Peer-Directory-Version=2, local=2 → no extra poll

---

## P2-09: Desktop — Read X-Peer-Directory-Version and Trigger Config Refresh

**Category:** Config Sync — Layer 1 (agent side)
**Phase:** 1
**Priority:** P0
**Depends on:** P2-04

### Files to Modify

**1. ConfigManager.cs — track local peerDirectoryVersion**

File: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/ConfigManager.cs`

Add field (around line 50):
```csharp
private long _currentPeerDirectoryVersion;
public long CurrentPeerDirectoryVersion => _currentPeerDirectoryVersion;
```

In `ApplyConfigAsync()` (line 73), update from the config response or stored value.

Add method:
```csharp
public bool IsPeerDirectoryStale(long cloudVersion) => cloudVersion > _currentPeerDirectoryVersion;
public void UpdatePeerDirectoryVersion(long version) => Interlocked.Exchange(ref _currentPeerDirectoryVersion, version);
```

**2. CloudUploadWorker.cs — read header after upload**

File: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CloudUploadWorker.cs`
Method: `SendWithRetryAsync()`, after line 213 (response received).

Add:
```csharp
if (response.Headers.TryGetValues("X-Peer-Directory-Version", out var values)
    && long.TryParse(values.FirstOrDefault(), out var peerDirVersion)
    && _configManager.IsPeerDirectoryStale(peerDirVersion))
{
    _cadenceController.RequestImmediateConfigPoll();
}
```

**3. ConfigPollWorker.cs — read header after config poll**

File: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/ConfigPollWorker.cs`
Method: `SendPollRequestAsync()`, after line 165 (ETag extraction).

Add peer directory version extraction and update in ConfigManager.

**4. CadenceController.cs — add immediate config poll trigger**

File: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Runtime/CadenceController.cs`

Add method:
```csharp
private volatile bool _immediateConfigPollRequested;

public void RequestImmediateConfigPoll()
{
    _immediateConfigPollRequested = true;
    _wakeSignal.Release();  // wake the cadence loop
}
```

In `RunCycleAsync()`, before the regular tick work, check:
```csharp
if (_immediateConfigPollRequested)
{
    _immediateConfigPollRequested = false;
    await RunConfigPollAsync(config, ct);
}
```

**5. SyncStateRecord — persist version**

File: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Buffer/Entities/SyncStateRecord.cs`

Add:
```csharp
public long PeerDirectoryVersion { get; set; }
```

Add EF migration for the new column.

### Acceptance Criteria

- [ ] CloudUploadWorker reads X-Peer-Directory-Version from upload responses
- [ ] ConfigPollWorker reads X-Peer-Directory-Version from config responses
- [ ] CommandPollWorker reads X-Peer-Directory-Version from command responses
- [ ] Stale version triggers immediate config poll via CadenceController
- [ ] PeerDirectoryVersion persists across app restart
- [ ] Unit test: upload response with version 5, local version 3 → immediate config poll triggered

---

## P2-10: Reduce Android Config Poll Interval for HA-Enabled Sites

**Category:** Config Sync
**Phase:** 1
**Priority:** P1
**Depends on:** None

### Problem

Android default `configPollIntervalSeconds` is 300 seconds (5 minutes). For HA-enabled sites, this is the fallback if Layers 1-2 both fail.

### Files to Modify

**1. Cloud — GetAgentConfigHandler.cs**

File: `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs`

When building the config response for an HA-enabled site (`siteHa.enabled = true`), set `configPollIntervalSeconds` to 60 (or make it configurable) instead of the default 300.

This is in the sync section of the config response (around the area where defaults are applied).

**2. Android — CadenceController.kt**

File: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/runtime/CadenceController.kt`
Line 116: `configPollTickFrequency` — verify that when cloud delivers `configPollIntervalSeconds=60`, the tick frequency adjusts accordingly.

### Acceptance Criteria

- [ ] HA-enabled sites receive `configPollIntervalSeconds=60` in their config
- [ ] Non-HA sites retain the default 300-second interval
- [ ] Android CadenceController correctly adjusts tick frequency for the reduced interval

---

## P2-11: Reduce Cloud Config Cache TTL for HA-Enabled Sites

**Category:** Config Sync
**Phase:** 1
**Priority:** P1
**Depends on:** None

### Files to Modify

**1. GetAgentConfigHandler.cs**

File: `src/cloud/FccMiddleware.Application/AgentConfig/GetAgentConfigHandler.cs`
Line 61: Cache key with 60-second TTL.

Change: when the site has `siteHa.enabled = true`, use a 30-second cache TTL instead of 60 seconds. This ensures peer directory changes surface faster in config responses.

Option A: Use a different cache duration based on HA status.
Option B: Reduce cache TTL to 30 seconds for all sites (simplest — the cache is lightweight).

### Acceptance Criteria

- [ ] Config cache TTL is 30 seconds for HA-enabled sites
- [ ] Peer directory changes are visible in config responses within 30 seconds of the database change

---

## P2-12: LAN UDP Peer Announcement — Broadcast on Startup

**Category:** Config Sync — Layer 3
**Phase:** 2
**Priority:** P1
**Depends on:** P2-03

### Design

When an agent starts up or completes registration, it broadcasts a UDP datagram on the station LAN:

```json
{
  "type": "PEER_ANNOUNCE",
  "agentId": "device-abc-123",
  "siteCode": "MW-BT001",
  "peerApiHost": "192.168.1.50",
  "peerApiPort": 8586,
  "peerDirectoryVersion": 5
}
```

Broadcast address: `255.255.255.255` (or subnet broadcast) on a fixed port (e.g., 18586).

### Files to Create / Modify

**1. Android — new class: LanPeerAnnouncer.kt**

Create: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/peer/LanPeerAnnouncer.kt`

- Uses `DatagramSocket` to send UDP broadcast
- Called after registration completes and on each app startup when `siteHa.enabled`
- Called when agent role changes (e.g., promoted to PRIMARY)
- Include in AppModule.kt DI registration

**2. Desktop — new class: LanPeerAnnouncer.cs**

Create: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Peer/LanPeerAnnouncer.cs`

- Uses `UdpClient` to send UDP broadcast
- Called after registration completes and on each app startup when `SiteHaEnabled`
- Register in DI via `AddAgentCore()`

### Acceptance Criteria

- [ ] Agent broadcasts UDP announcement on startup when `siteHa.enabled = true`
- [ ] Agent broadcasts UDP announcement after registration completes
- [ ] Broadcast is best-effort (no error on send failure — LAN may not support broadcast)
- [ ] Broadcast includes agentId, siteCode, peer API endpoint, and peerDirectoryVersion
- [ ] No broadcast when `siteHa.enabled = false`

---

## P2-13: LAN UDP Peer Announcement — Listener

**Category:** Config Sync — Layer 3
**Phase:** 2
**Priority:** P1
**Depends on:** P2-12

### Files to Create / Modify

**1. Android — new class: LanPeerListener.kt**

Create: `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/peer/LanPeerListener.kt`

- Binds `DatagramSocket` to the announcement port (e.g., 18586)
- Runs in a coroutine within the foreground service
- On receiving a valid PEER_ANNOUNCE from a different agentId at the same siteCode:
  - Add peer to local peer cache (temporary, until next cloud config confirms)
  - Trigger immediate config poll to get authoritative peer directory
- Ignore announcements from self or different siteCode
- Only active when `siteHa.enabled`

**2. Desktop — new class: LanPeerListener.cs**

Create: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Peer/LanPeerListener.cs`

- Uses `UdpClient` to listen on the announcement port
- Runs as a background task within the cadence controller or as a separate hosted service
- Same logic: on valid announcement, trigger config refresh

### Acceptance Criteria

- [ ] Agent listens for UDP peer announcements when `siteHa.enabled = true`
- [ ] Receiving a valid announcement from a new peer triggers immediate config poll
- [ ] Announcements from self are ignored
- [ ] Announcements from different siteCode are ignored
- [ ] Listener does not crash if the port is already in use or UDP is blocked
- [ ] Integration test: two agents on same LAN, agent B starts → agent A detects within 5 seconds

---

## P2-14: Define Election Algorithm Explicitly

**Category:** Design
**Phase:** 0
**Priority:** P0
**Depends on:** None

### Deliverable

Add a new subsection to MultiAgents.md (under §12, Phase 5) with the explicit election algorithm. Recommended algorithm for small clusters (2-3 agents):

```
1. Standby detects primary failure:
   - Missed heartbeats exceed failoverTimeoutSeconds threshold
   - Direct health probe to primary fails

2. Enter CANDIDATE state

3. Pre-election checks:
   - Verify no higher-priority healthy peer exists (send health probe to all known peers)
   - Verify own replication lag is within maxReplicationLagSeconds
   - Verify own config version matches or exceeds primary's last known version

4. Wait a randomized back-off:
   - Base delay = (1000 - priority) * 10ms (higher priority = shorter wait)
   - Add random jitter: 0-500ms
   - This ensures higher-priority agents claim first

5. Broadcast leadership claim:
   - newEpoch = max(all locally seen epochs) + 1
   - Sign claim with peer shared secret
   - Send to all known peers via POST /peer/claim-leadership

6. Wait electionTimeoutSeconds for responses:
   - If higher-priority claim received → revert to STANDBY_HOT
   - If rejection received (peer sees a valid higher-epoch leader) → revert to STANDBY_HOT
   - If no objection within timeout → assume leadership

7. On assuming leadership:
   - Persist new epoch locally
   - Open FCC session
   - Begin authoritative operations
   - Report new epoch to cloud on next contact

8. If cloud is unreachable during election:
   - Election proceeds purely peer-to-peer
   - New epoch is registered with cloud on next successful contact
   - Cloud accepts any epoch > its current record (Option B — see P2-15)
```

### Acceptance Criteria

- [ ] Algorithm is documented in MultiAgents.md with numbered steps
- [ ] Priority-based back-off timing is specified
- [ ] Quorum behavior for 2-agent vs 3-agent clusters is documented
- [ ] Cloud-unreachable election path is documented
- [ ] Test matrix scenario "Two agents boot at the same time" references this algorithm

---

## P2-15: Define Cloud Role During Elections (Option B)

**Category:** Design
**Phase:** 0-1
**Priority:** P0
**Depends on:** P2-14

### Problem

The cloud's `SiteHaLeadershipResolver` computes leader deterministically, but agent-side elections may produce a different result (especially during internet outages). These two mechanisms can conflict.

### Recommendation: Option B — Agent Elections Are Authoritative

The cloud accepts any epoch strictly higher than what it has previously seen. The cloud does not initiate or veto elections. The cloud's resolver reflects agent-reported state.

### Files to Modify

**1. AuthoritativeWriteFenceService.cs**

File: `src/cloud/FccMiddleware.Api/Infrastructure/AuthoritativeWriteFenceService.cs`
Method: `ValidateAsync()`, lines 81-142.

Current logic (around line 92): compares `requestLeaderEpoch` vs the cloud-computed `leadership.LeaderEpoch`.

Change: instead of computing leader from the resolver, trust the agent's declared epoch if it is ≥ the highest epoch the cloud has seen for this site. The cloud records the new epoch and the claiming agent as leader.

Specifically:
- If `requestLeaderEpoch > siteMaxEpoch` → accept the write, update the site's recorded leader and epoch
- If `requestLeaderEpoch == siteMaxEpoch` and `requestDeviceId == recordedLeader` → accept
- If `requestLeaderEpoch == siteMaxEpoch` and `requestDeviceId != recordedLeader` → reject (stale writer)
- If `requestLeaderEpoch < siteMaxEpoch` → reject (stale epoch)

**2. SiteHaLeadershipResolver.cs**

File: `src/cloud/FccMiddleware.Application/AgentConfig/SiteHaLeadershipResolver.cs`

Change `ResolveSnapshot()` to use the **recorded** leader and epoch from the site/agent registry, not compute them from priority ordering. Priority ordering is only used for initial assignment (cold start, no epoch exists).

**3. Document in MultiAgents.md**

Add to Phase 5 section: "The cloud is a passive observer of elections, not an arbiter. Agent-side elections are authoritative. The cloud records the result and enforces it for write fencing."

### Acceptance Criteria

- [ ] Cloud accepts writes with epoch > current max epoch, regardless of which agent sends them
- [ ] Cloud rejects writes with epoch < current max epoch
- [ ] Cloud rejects writes with epoch == current max epoch from non-leader agent
- [ ] Cloud updates its recorded leader when a higher epoch arrives
- [ ] Elections work when cloud is unreachable (agent registers epoch on next contact)
- [ ] Unit test: agent A is leader at epoch 3, agent B claims epoch 4 → cloud accepts B's writes, rejects A's

---

## P2-16: FCC Session Handover Research Per Vendor

**Category:** Research
**Phase:** 0
**Priority:** P0
**Depends on:** None

### Deliverable

For each supported FCC vendor, document the answers to:

| Question | DOMS | Radix | Advatec | Petronite |
|---|---|---|---|---|
| Can two clients hold simultaneous sessions? | ? | ? | ? | ? |
| What happens to old session when new client connects? | ? | ? | ? | ? |
| Session establishment time (seconds) | ? | ? | ? | ? |
| Does FCC queue commands during session gap? | ? | ? | ? | ? |
| Is there a "force disconnect" or "session takeover" API? | ? | ? | ? | ? |
| Does FCC notify old client of session termination? | ? | ? | ? | ? |

### Where to Store

Create: `docs/specs/research/fcc-session-handover.md`

### Impact on Plan

Results gate Phase 5 (automatic failover) design:
- If FCC allows only one session: new primary must wait for old session to timeout
- If FCC allows concurrent sessions: need FCC-level fencing (not just cloud-level)
- Session establishment time directly reduces the 30-second failover budget

### Acceptance Criteria

- [ ] All four vendors have documented session behavior
- [ ] DOMS is fully documented (MVP vendor, highest priority)
- [ ] Session establishment time is measured, not estimated
- [ ] Findings are referenced in MultiAgents.md Phase 5

---

## P2-17: Define In-Flight Pre-Auth Handling During Failover

**Category:** Design
**Phase:** 0
**Priority:** P0
**Depends on:** None

### Deliverable

Add a subsection to MultiAgents.md Phase 5 (or Phase 3 replication) covering:

**1. Replication of pre-auth state**

Pre-auth records in non-terminal states (PENDING, AUTHORIZED, DISPENSING) must be replicated to standby in near-real-time. The delta sync interval used for transactions is not fast enough — pre-auths change state rapidly.

Recommendation: replicate pre-auth state changes via the peer heartbeat (piggybacked as a lightweight delta). Each heartbeat carries:
- List of active pre-auth IDs and their current states
- Any pre-auth state changes since last heartbeat

**2. On promotion (new primary adopts pre-auths)**

- All non-terminal pre-auths from the replicated state become owned by the new primary
- PENDING pre-auths: the new primary does NOT re-send to FCC (risk of double-authorization). Instead, mark as UNKNOWN and wait for FCC to either report a dispense or for the pre-auth to expire.
- AUTHORIZED pre-auths: the new primary monitors for the dispense-complete event from FCC
- DISPENSING pre-auths: the new primary monitors for completion

**3. Test scenarios to add to §15**

| Scenario | Expected Result |
|---|---|
| Primary fails with PENDING pre-auth (no FCC response yet) | New primary marks pre-auth UNKNOWN; FCC either completes or times out |
| Primary fails with AUTHORIZED pre-auth (pump live) | New primary monitors FCC for dispense-complete; matches to replicated pre-auth |
| Primary fails with DISPENSING pre-auth (fuel flowing) | New primary captures dispense-complete and reconciles |

### Acceptance Criteria

- [ ] Pre-auth replication mechanism is documented (heartbeat piggyback or dedicated sync)
- [ ] New primary adoption behavior is documented for each pre-auth state
- [ ] PENDING pre-auth is explicitly addressed (no re-send, mark UNKNOWN)
- [ ] Three new test scenarios added to §15 test matrix

---

## P2-18: Document Ingestion Mode × Failover Interaction

**Category:** Design
**Phase:** 0
**Priority:** P1
**Depends on:** None

### Deliverable

Add a subsection to MultiAgents.md Phase 5 documenting how failover interacts with each ingestion mode:

**CLOUD_DIRECT (default):**
- Failover is transparent to the FCC. The FCC pushes to the cloud regardless of which agent is primary.
- The new primary takes over LAN catch-up polling and pre-auth handling.
- No FCC reconfiguration needed.

**RELAY:**
- The FCC is configured to push to the primary agent's IP. On failover, the FCC's push target becomes unreachable.
- Options: (a) shared VIP/floating IP on LAN, (b) FCC manual reconfiguration by site supervisor, (c) accept push interruption — LAN catch-up poll by new primary handles gap.
- Recommendation: Option (c) for MVP. Document that RELAY mode has degraded failover (push interrupted, catch-up poll still works).

**BUFFER_ALWAYS:**
- Stranded buffer on failed primary contains un-synced transactions.
- Risk window = sync interval × transaction rate.
- Mitigation: replication (Phase 3) reduces the risk to replication lag × transaction rate.
- Document acceptable risk threshold.

### Acceptance Criteria

- [ ] Each ingestion mode's failover behavior is documented
- [ ] RELAY mode limitations are explicitly called out
- [ ] Risk quantification for BUFFER_ALWAYS stranded buffer is documented

---

## P2-19: Define Clock Synchronization Approach

**Category:** Design
**Phase:** 2
**Priority:** P1
**Depends on:** None

### Deliverable

Add to MultiAgents.md Phase 2 (peer connectivity):

1. **Replication ordering:** Use monotonic sequence numbers (not wall-clock timestamps) for all replication. The primary assigns sequences; standbys apply in order.

2. **Failure detection:** Use heartbeat round-trip measurement (elapsed time since last successful heartbeat response), not absolute timestamps. This is already implied by "missed heartbeat count" but should be explicit.

3. **Clock skew detection:** Add a `peerClockOffsetMs` field to the heartbeat response. Each agent includes its local UTC time; the receiver computes the offset. If offset exceeds 30 seconds, log a warning in diagnostics.

4. **No dependency on NTP:** The HA system must work correctly even if NTP is unavailable for extended periods (common during internet outages in African fuel stations).

### Files to Modify

- MultiAgents.md: add to Phase 2 section
- MA-2.1 peer API contract: add clock offset to heartbeat response
- MA-3.1 replication data model: confirm sequence numbers are used, not timestamps

### Acceptance Criteria

- [ ] Replication uses sequence numbers, not wall-clock timestamps
- [ ] Heartbeat failure detection uses elapsed time, not absolute timestamps
- [ ] Peer clock skew is detectable and logged
- [ ] No correctness dependency on NTP

---

## P2-20: Move Localhost Facade Contract Definition to Phase 2

**Category:** Design
**Phase:** 2
**Priority:** P1
**Depends on:** None

### Deliverable

Move the **contract definition** (not implementation) from MA-4.1 into Phase 2, alongside MA-2.1 (Peer API contract).

Define:
- Which localhost endpoints proxy to primary when local agent is standby
- Which endpoints serve from local replicated cache
- Freshness threshold for cache-vs-proxy decision
- Error behavior when primary is unreachable during proxy

This ensures the peer API (MA-2.1) includes the proxy endpoints (`POST /peer/proxy/preauth`, `GET /peer/proxy/pump-status`) with correct payload contracts from the start.

### Files to Modify

- MultiAgents.md: add proxy contract to MA-2.1 detailed instructions
- Move instructions 2-4 from MA-4.1 into the contract section (keep implementation in Phase 4)

### Acceptance Criteria

- [ ] Proxy contract is defined in Phase 2 alongside the peer API
- [ ] Peer API endpoints include proxy payloads
- [ ] Phase 4 implementation references the Phase 2 contract

---

## P2-21: Resolve READ_ONLY Runtime Role

**Category:** State Model
**Phase:** 0
**Priority:** P2
**Depends on:** None

### Problem

READ_ONLY is defined in §4.1 but never used in any phase, test, or acceptance criteria.

### Recommendation

Keep READ_ONLY but define its purpose explicitly:

READ_ONLY is assigned to agents with `roleCapability = READ_ONLY` in their cloud config. This is an operator-configured state for agents that should serve cached data but never participate in elections or become primary. Use case: a dedicated monitoring device at a site that shows transaction data but does not control the FCC.

### Files to Modify

- MultiAgents.md §4.1: add use case description
- `SiteHaLeadershipResolver.cs`: already excludes READ_ONLY from leader selection (no change needed)

### Acceptance Criteria

- [ ] READ_ONLY purpose is documented with a concrete use case
- [ ] Or: READ_ONLY is removed from the state model (if no use case is valid)

---

## P2-22: Add Rollback and Degradation Plan

**Category:** Operations
**Phase:** 1
**Priority:** P0
**Depends on:** None

### Deliverable

Add a new section to MultiAgents.md: "Emergency HA Disable and Rollback."

Content:

1. **Disable HA for a site:** Set `siteHa.enabled = false` in the cloud config for the site. On next config poll, all agents:
   - Stop peer heartbeats and replication
   - Stop LAN announcements and listening
   - The current leader remains PRIMARY
   - All standbys stop standby behavior and operate independently (each polls FCC, each buffers locally, cloud deduplicates)

2. **Which agent is PRIMARY after HA disable:** The agent that was PRIMARY at the moment HA was disabled remains PRIMARY. If the cloud cannot determine the current primary (e.g., leader epoch is stale), fall back to priority-based assignment.

3. **Cloud dashboard emergency button:** Add a "Disable Site HA" action in the portal that sets `siteHa.enabled = false` and enqueues `REFRESH_CONFIG` for all agents at the site.

4. **Feature flag for gradual rollout:** Use `siteHa.autoFailoverEnabled` independently of `siteHa.enabled`. This allows enabling peer discovery and replication (HA-lite) without enabling automatic failover. Planned switchover still works.

### Files to Modify

- MultiAgents.md: add new section
- Cloud portal (future): add HA disable button
- Both agents: verify that `siteHa.enabled = false` correctly disables all peer traffic

### Acceptance Criteria

- [ ] Rollback procedure is documented
- [ ] `siteHa.enabled = false` stops all peer traffic within one config poll cycle
- [ ] No data loss occurs when HA is disabled mid-operation
- [ ] Feature flag strategy (enabled vs autoFailoverEnabled) is documented

---

## P2-23: Add Failover Alerting Rules

**Category:** Operations
**Phase:** 1
**Priority:** P1
**Depends on:** None

### Deliverable

Add to MultiAgents.md MA-1.3 (operational visibility) or MA-6.2 (operations):

| Alert | Severity | Trigger | Recipients |
|---|---|---|---|
| Failover occurred | CRITICAL | New leader epoch detected for site | Ops Manager, Site Supervisor |
| No promotable standby | CRITICAL | Site has only one active agent, or all standbys have lag > threshold | Ops Manager |
| Replication lag high | WARNING | Any standby's `lastReplicationLagSeconds > maxReplicationLagSeconds * 0.8` | Ops Manager |
| Election flapping | WARNING | More than 2 leader changes within 10 minutes | Ops Manager |
| Stale-writer rejection | CRITICAL | `AuthoritativeWriteFenceService` rejects a write | Ops Manager |
| Peer directory stale | WARNING | Agent's peerDirectoryVersion is behind cloud by > 5 minutes | Ops team |

### Acceptance Criteria

- [ ] Alert rules are documented with severity, trigger, and recipients
- [ ] Alerts can be delivered via configurable channels (webhook, email, dashboard)

---

## P2-24: Define Cold-Start Election Behavior

**Category:** Design
**Phase:** 1
**Priority:** P1
**Depends on:** P2-14, P2-15

### Deliverable

Add to the election algorithm (P2-14):

**Cold start (no prior epoch exists for the site):**

1. On first boot, agent checks with cloud for current leadership (`GET /api/v1/agent/config` returns peer directory with leader info).
2. If cloud returns a leader with valid epoch → agent adopts that leader and enters appropriate role.
3. If cloud returns no leader (first agent at site, epoch 0) → agent claims epoch 1 and becomes PRIMARY.
4. If two agents boot simultaneously and both see epoch 0:
   - Both attempt to claim epoch 1
   - Cloud accepts the first write with epoch 1 and records that agent as leader
   - Second agent's write is rejected (same epoch, different agent) → it falls back to STANDBY_HOT and fetches updated config
5. If cloud is unreachable on first boot:
   - Agent uses priority-based tie-breaking with randomized back-off (same as P2-14 step 4)
   - Registers epoch with cloud on first successful contact

### Acceptance Criteria

- [ ] Cold-start behavior is documented for single agent, two simultaneous agents, and cloud-unreachable scenarios
- [ ] No split-brain possible during cold start
- [ ] Test scenario added to §15 test matrix

---

## P2-25: Map All SiteHa Config Fields to Desktop AgentConfiguration

**Category:** Desktop Agent
**Phase:** 1
**Priority:** P1
**Depends on:** None

### Problem

Currently only `LeaderEpoch` is mapped from `SiteConfig.SiteHa` to `AgentConfiguration`. Other fields (priority, role, heartbeat interval, failover timeout, etc.) are defined in the model but never applied.

### Files to Modify

**ConfigManager.cs**

File: `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/ConfigManager.cs`
Method: `ApplyHotReloadFields()`, lines 338-341.

Expand to map all SiteHa fields:
```csharp
if (source.SiteHa is not null)
{
    target.SiteHaEnabled = source.SiteHa.Enabled;
    target.AutoFailoverEnabled = source.SiteHa.AutoFailoverEnabled;
    target.SiteHaPriority = source.SiteHa.Priority;
    target.RoleCapability = source.SiteHa.RoleCapability;
    target.CurrentRole = source.SiteHa.CurrentRole;
    target.HeartbeatIntervalSeconds = source.SiteHa.HeartbeatIntervalSeconds;
    target.FailoverTimeoutSeconds = source.SiteHa.FailoverTimeoutSeconds;
    target.MaxReplicationLagSeconds = source.SiteHa.MaxReplicationLagSeconds;
    target.ReplicationEnabled = source.SiteHa.ReplicationEnabled;
    target.ProxyingEnabled = source.SiteHa.ProxyingEnabled;
    target.LeaderEpoch = source.SiteHa.LeaderEpoch;
    target.PeerApiPort = source.SiteHa.PeerApiPort;
}
```

### Acceptance Criteria

- [ ] All SiteHa fields from cloud config are applied to the runtime AgentConfiguration
- [ ] Hot-reload of SiteHa fields takes effect without app restart
- [ ] Unit test: apply config with `SiteHaEnabled=true, Priority=50` → verify AgentConfiguration reflects values

---

## P2-26: Android Background Execution Verification on Urovo i9100

**Category:** Research
**Phase:** 2
**Priority:** P0
**Depends on:** None

### Deliverable

Test and document on actual Urovo i9100 hardware:

1. **Foreground service reliability:** Does the Android foreground service survive 24+ hours without being killed by the OS?
2. **Doze mode impact:** With the device idle (screen off) for 30+ minutes, can the foreground service still send UDP/HTTP requests every 5 seconds?
3. **Battery optimization:** Does the Urovo i9100 OEM ROM have aggressive battery optimization that kills foreground services? What exemptions are needed?
4. **Minimum reliable heartbeat interval:** What is the shortest interval at which the agent can reliably send/receive peer heartbeats while in foreground service mode?
5. **Promotion test:** Device idle for 30 minutes → primary fails → does promotion happen within 30 seconds?

### Where to Store

Create: `docs/specs/research/urovo-i9100-background-constraints.md`

### Acceptance Criteria

- [ ] All 5 questions answered with test results from real hardware
- [ ] Minimum heartbeat interval is documented and used to set `heartbeatIntervalSeconds` floor
- [ ] Required Android battery exemption settings are documented for deployment runbook

---

## P2-27: Add FCC Vendor Takeover Test Scenarios

**Category:** Testing
**Phase:** 2
**Priority:** P1
**Depends on:** P2-16

### Deliverable

Add to `docs/specs/testing/testing-strategy.md`:

New test category: **FCC Session Takeover (per vendor)**

| Scenario | Vendor | Expected Result |
|---|---|---|
| New primary connects while old session is active | DOMS | Document actual behavior (from P2-16 research) |
| New primary connects after old primary crash (no graceful close) | DOMS | Document actual behavior |
| Two agents connect simultaneously | DOMS | Document: does FCC accept both? reject second? |
| Primary sends pre-auth, crashes, new primary connects | DOMS | Document: is pre-auth still active on FCC? |

Repeat for each vendor as they are onboarded.

### Acceptance Criteria

- [ ] Test scenarios added to testing strategy
- [ ] DOMS scenarios are executable in virtual lab
- [ ] Results from P2-16 research inform expected results

---

## P2-28: Add peerDirectoryVersion to Test Factories and Contract Tests

**Category:** Testing
**Phase:** 1
**Priority:** P1
**Depends on:** P2-03, P2-08, P2-09

### Files to Modify

**1. Android — TestConfigFactory.kt**

File: `src/edge-agent/app/src/test/kotlin/com/fccmiddleware/edge/config/TestConfigFactory.kt`
Method: `canonicalEdgeConfig()`, lines 3-122.

Add `peerDirectoryVersion` to the SiteHa section of the test config.

**2. Android — EdgeAgentConfigContractTest.kt**

File: `src/edge-agent/app/src/test/kotlin/com/fccmiddleware/edge/config/EdgeAgentConfigContractTest.kt`

Add test: verify `peerDirectoryVersion` is parsed from config JSON.

**3. Android — CloudUploadWorkerTest.kt**

File: `src/edge-agent/app/src/test/kotlin/com/fccmiddleware/edge/sync/CloudUploadWorkerTest.kt`

Add test: mock upload response with `X-Peer-Directory-Version` header, verify version is extracted.

**4. Cloud — TransactionsControllerTests.cs**

File: `src/cloud/FccMiddleware.Api.Tests/Controllers/TransactionsControllerTests.cs`

Add test: verify upload response includes `X-Peer-Directory-Version` header.

**5. Cloud — AuthoritativeWriteFenceServiceTests.cs**

File: `src/cloud/FccMiddleware.Api.Tests/Infrastructure/AuthoritativeWriteFenceServiceTests.cs`

Add tests for Option B epoch validation:
- Higher epoch from new agent → accepted
- Same epoch from non-leader → rejected
- Lower epoch → rejected

**6. Desktop — unit tests for header checking**

Add tests verifying CloudUploadWorker and ConfigPollWorker read the X-Peer-Directory-Version header correctly.

### Acceptance Criteria

- [ ] Android contract test validates peerDirectoryVersion parsing
- [ ] Android upload worker test validates header extraction
- [ ] Cloud transaction controller test validates header presence in responses
- [ ] Cloud fence service tests cover Option B epoch validation
- [ ] Desktop tests validate header extraction and config poll triggering

---

## Implementation Order

The recommended implementation order respects dependencies:

```
Phase 0 (Documentation / Design — no code):
  P2-01  Add CANDIDATE state
  P2-02  Update plan text for Android-only
  P2-14  Define election algorithm
  P2-15  Define cloud role (Option B)
  P2-16  FCC session handover research
  P2-17  In-flight pre-auth handling
  P2-18  Ingestion mode × failover
  P2-21  Resolve READ_ONLY role
  P2-22  Rollback plan

Phase 1 (Config Sync — code changes):
  P2-03  Cloud: peerDirectoryVersion column         ← start here
  P2-04  Cloud: X-Peer-Directory-Version middleware  ← depends on P2-03
  P2-05  Cloud: increment version on events          ← depends on P2-03
  P2-06  Cloud+agents: REFRESH_CONFIG command type   ← independent
  P2-07  Cloud: enqueue REFRESH_CONFIG               ← depends on P2-05, P2-06
  P2-08  Android: read header + trigger refresh      ← depends on P2-04
  P2-09  Desktop: read header + trigger refresh      ← depends on P2-04
  P2-10  Android: reduce config poll interval        ← independent
  P2-11  Cloud: reduce cache TTL                     ← independent
  P2-23  Alerting rules                              ← independent
  P2-24  Cold-start election behavior                ← depends on P2-14
  P2-25  Desktop: map all SiteHa fields              ← independent
  P2-28  Tests                                       ← depends on P2-03 through P2-09

Phase 2 (LAN + Research):
  P2-12  LAN UDP announcer                           ← independent
  P2-13  LAN UDP listener                            ← depends on P2-12
  P2-19  Clock synchronization approach              ← independent
  P2-20  Localhost facade contract                   ← independent
  P2-26  Urovo background execution research         ← independent
  P2-27  FCC takeover test scenarios                 ← depends on P2-16
```

### Parallelization

These groups can run in parallel:
- **Group A (cloud):** P2-03 → P2-04 → P2-05 → P2-07
- **Group B (cloud):** P2-06 (command type, independent)
- **Group C (Android):** P2-08 (after P2-04 is deployed)
- **Group D (Desktop):** P2-09 + P2-25 (after P2-04 is deployed)
- **Group E (Design):** P2-14, P2-15, P2-16, P2-17, P2-18 (all documentation, no code dependency)
