# Multi-Agent Site - Phasewise Implementation Plan

**Status:** In Progress
**Date:** 2026-03-14
**Scope:** Post-MVP implementation plan for desktop and Android Edge Agents running in parallel with a single active primary and automatic failover
**Sprint Cadence:** 2-week sprints

## Repository Verification Snapshot

Current repo verification as of 2026-03-14:

- Phase 0 is **partially implemented**.
  - Requirements, HLD, config schema, and testing-strategy updates for multi-agent HA are present.
  - The requested explicit `CANDIDATE` state diagram and fully aligned desktop/Android runtime-state documentation are still incomplete.
- Phase 1 is **partially implemented**.
  - Cloud registration, site-agent membership fields, peer-directory bootstrap metadata, and leader/epoch visibility are implemented.
  - Failover-specific audit/event history and operator-visible failover screens are still incomplete.
- Phase 2 is **not yet implemented in runtime code**.
  - Peer API hosting, peer heartbeat exchange, peer discovery, and peer diagnostics still need end-to-end implementation.
- Phase 3 is **not yet implemented in runtime code**.
  - Warm replication, snapshot/delta sync, and promotable-standby readiness gates are still pending.
- Phase 4 is **partially implemented**.
  - The localhost contract is preserved in requirements and current local APIs.
  - Remote-primary proxying, planned switchover controls, and compatibility regression coverage are still pending.
- Phase 5 is **partially implemented in runtime code**.
  - Cloud-side stale-writer fencing is now enforced for authoritative transaction upload and pre-auth forward APIs, and desktop/Android writers now carry `leaderEpoch`.
  - Automatic suspect detection, election, epoch claim, self-demotion, and recovery/rejoin logic are still pending.
- Phase 6 is **not yet implemented**.
  - Chaos/soak validation, full multi-agent operations runbooks, and pilot rollout controls are still pending.

## 1. Objective

Implement a site-level high-availability model where:

- one or more Edge Agents (Android and/or Desktop) can run in parallel at a site
- exactly one eligible online agent is `PRIMARY` at a time
- all other agents run as `STANDBY`
- the primary can fail without site-level service interruption
- a standby can be promoted automatically
- a recovered node rejoins as standby and does not auto-preempt the current leader

## 2. Fixed Design Decisions

These decisions should be treated as baseline constraints for the implementation plan:

1. Use active-standby, not active-active FCC control.
2. Prefer desktop as primary where available.
3. Keep the Android localhost API stable so Odoo POS continues to talk to `localhost` on each HHT.
4. Use epoch-based leader fencing for failover.
5. Require warm replication before automatic promotion is enabled.
6. Do not auto-failback when a former primary returns.

## 3. Success Criteria

The implementation is complete only when all of the following are true:

- automatic failover completes within `30s` of confirmed primary failure
- no buffered transaction or pre-auth record is lost during failover
- no duplicate FCC command execution occurs during switchover or failover drills
- standby replication lag is measurable and enforced as a promotion gate
- stale primaries are fenced by epoch and rejected by the cloud when they attempt authoritative writes
- Odoo POS continues to use the same localhost contract on all HHTs

## 4. Roles, States, and Ownership

### 4.1 Runtime roles

- `PRIMARY`
  - owns FCC communication
  - owns authoritative transaction ingestion
  - owns pre-auth execution
  - owns authoritative cloud upload stream
  - publishes peer heartbeats and replication updates
- `STANDBY_HOT`
  - maintains a warm replica
  - proxies live work to primary
  - is eligible for promotion
- `CANDIDATE`
  - has detected primary failure and is attempting to claim leadership
  - cannot serve authoritative writes or accept FCC commands
  - broadcasts a signed leadership claim with incremented epoch
  - exits to PRIMARY on successful claim, or STANDBY_HOT if a higher-priority claim is observed or timeout expires
  - multiple simultaneous CANDIDATEs are allowed; at most one transitions to PRIMARY
- `RECOVERING`
  - catching up after restart or rejoin
  - not eligible for promotion until replication lag is within threshold
- `READ_ONLY`
  - can serve cached reads
  - cannot become primary
  - assigned to agents with `roleCapability = READ_ONLY` in their cloud config
  - use case: a dedicated monitoring device at a site that displays transaction data and pump status but does not control the FCC or participate in elections (e.g., a supervisor tablet or wall-mounted dashboard)
- `OFFLINE`
  - not participating in elections or replication

#### State transitions

```
                    ┌──────────────┐
       ┌───────────►│  STANDBY_HOT │◄──────────────┐
       │            └──────┬───────┘               │
       │                   │ primary failure        │ timeout / higher
       │                   │ detected               │ priority claim
       │                   ▼                        │
       │            ┌──────────────┐                │
       │            │  CANDIDATE   ├────────────────┘
       │            └──────┬───────┘
       │                   │ claim accepted
       │                   ▼
  ┌────┴───┐        ┌──────────────┐
  │RECOVERY│◄───────│   PRIMARY    │
  │  ING   │ restart└──────────────┘
  └────────┘
```

- `STANDBY_HOT → CANDIDATE`: triggered when heartbeat timeout expires for the current primary
- `CANDIDATE → PRIMARY`: leadership claim accepted (highest-priority candidate with valid epoch)
- `CANDIDATE → STANDBY_HOT`: timeout expires, or a higher-priority candidate's claim is observed
- `PRIMARY → RECOVERING`: agent restarts or loses quorum; rejoins as recovering, never auto-preempts
- `RECOVERING → STANDBY_HOT`: replication lag falls within `maxReplicationLagSeconds` threshold

### 4.2 Primary selection policy

Default site priority order (configuration-driven per site):

1. Desktop agent (if present)
2. Android agents by configured priority

Valid topologies:

- 1 Android agent (no HA — trivially PRIMARY)
- 2+ Android agents, no desktop (full HA between Android peers)
- 1 Desktop + 1+ Android agents (desktop-preferred PRIMARY)
- 1 Desktop agent only (no HA — trivially PRIMARY)

This remains configuration-driven because some sites may require Android-first leadership.

## 5. Cross-Application Workstreams

| Workstream | Primary Components | Responsibility |
|---|---|---|
| Control Plane | Cloud Backend, config contracts, registration | Multi-agent identity, leader epoch, peer directory, config |
| Peer Runtime | Android agent, desktop agent | Discovery, heartbeat, election, replication, proxying |
| Local Integration | Android agent, Odoo integration surface | Preserve localhost APIs while enabling remote primary ownership |
| Operations | Cloud dashboard, diagnostics, runbooks | Visibility, manual switchover, troubleshooting, rollout |

## 6. Phase Overview

| Phase | Name | Main Outcome | Status |
|---|---|---|---|
| 0 | Architecture and Contracts | Multi-agent behavior is specified and testable | Partial |
| 1 | Site Agent Identity and Registry | Multiple agents per site are first-class in cloud and config | Partial |
| 2 | Peer Connectivity and Discovery | Agents can find each other, authenticate, and exchange health | Pending |
| 3 | Replication and Standby Readiness | Standbys hold a warm, promotable copy of site state | Pending |
| 4 | Localhost Facade and Planned Switchover | Android HHTs hide the real primary and support operator-driven switchover | Partial |
| 5 | Automatic Failover and Recovery | Promotion, fencing, and rejoin behavior are automated | Partial |
| 6 | Hardening and Rollout | Failover is proven under fault conditions and ready for pilots | Pending |

> **Note:** All phases must work for Android-only sites. Desktop is optional.

## 7. Phase 0 - Architecture and Contracts (Sprints 1-2)

### Goal

Turn the current conceptual design into explicit product, API, config, and state-machine contracts before runtime work starts.

### MA-0.1: Requirements and HLD alignment

**Components:** Requirements, HLD, Android HLD, desktop HLD, runbooks
**Prereqs:** None
**Estimated effort:** 3 days

**Detailed instructions:**

1. Update `Requirements.md` to replace the manual-primary-only assumption with a post-MVP requirement set for:
   - multi-agent operation
   - automatic failover
   - planned switchover
   - standby replication
   - split-brain prevention
2. Update `HighLevelRequirements.md` to define:
   - desktop and Android parallel operation
   - the single-primary invariant
   - desktop-preferred priority policy
   - automatic promotion and non-preemptive recovery
3. Update `WIP-HLD-Edge-Agent.md` and desktop HLD artifacts so the runtime states, peer APIs, and leadership rules are consistent across platforms.
4. Add a concise state diagram for `PRIMARY -> STANDBY_HOT -> CANDIDATE -> RECOVERING`.

**Acceptance criteria:**

- no existing doc still describes automatic failover as undefined for the target release
- the single-primary invariant is explicit across requirements and HLD
- failover timeout target and stale-writer fencing are documented

### MA-0.2: Control-plane contract definition

**Components:** Cloud Backend, config schema, registration flow
**Prereqs:** MA-0.1
**Estimated effort:** 4 days

**Detailed instructions:**

1. Extend the edge config schema to replace static `agent.isPrimaryAgent` behavior with:
   - `siteHa.enabled`
   - `siteHa.autoFailoverEnabled`
   - `siteHa.priority`
   - `siteHa.roleCapability`
   - `siteHa.heartbeatIntervalSeconds`
   - `siteHa.failoverTimeoutSeconds`
   - `siteHa.maxReplicationLagSeconds`
   - `siteHa.peerDiscoveryMode`
   - `siteHa.allowFailback`
2. Extend registration contracts to include:
   - `deviceClass`
   - peer API endpoint metadata
   - capabilities
   - last known app version
3. Define cloud leader-fencing fields:
   - `leaderAgentId`
   - `leaderEpoch`
   - `leaderSinceUtc`
4. Define authoritative write APIs that must carry the current leader epoch.

**Acceptance criteria:**

- config schema can describe more than one agent per site
- leader epoch is part of the authoritative control-plane contract
- registration and config contracts distinguish desktop from Android devices

### MA-0.3: Test strategy and virtual lab extension

**Components:** Virtual lab, testing strategy, cloud/backend test harnesses
**Prereqs:** MA-0.1
**Estimated effort:** 3 days

**Detailed instructions:**

1. Define a virtual-lab topology with:
   - one FCC simulator
   - one desktop agent
   - at least two Android agents or Android simulators/mocks
2. Add failover scenarios to the testing strategy:
   - primary crash
   - network partition
   - stale primary recovery
   - replication lag promotion block
3. Define measurable assertions for:
   - election time
   - replication lag
   - duplicate command prevention
   - transaction continuity

**Acceptance criteria:**

- the project has a repeatable lab model for multi-agent testing
- failover scenarios are listed before implementation starts

## 8. Phase 1 - Site Agent Identity and Registry (Sprints 2-4)

### Goal

Make multi-agent membership a first-class site concept in the cloud and on every agent.

### MA-1.1: Cloud site-agent registry

**Components:** Cloud Backend, database, admin API
**Prereqs:** MA-0.2
**Estimated effort:** 1 sprint

**Detailed instructions:**

1. Add a site-agent registry model with fields such as:
   - `agentId`
   - `siteCode`
   - `deviceClass`
   - `capabilities`
   - `priority`
   - `role`
   - `leaderEpochSeen`
   - `lastHeartbeatUtc`
   - `lastReplicationLagSeconds`
   - `status`
2. Add a leader record per site:
   - `leaderAgentId`
   - `leaderEpoch`
   - `leaderSinceUtc`
   - `lastValidatedUtc`
3. Add failover audit events:
   - promotion
   - demotion
   - planned switchover
   - stale-writer rejection
4. Expose read APIs for:
   - site peer directory
   - current leader
   - recent failover events

**Acceptance criteria:**

- cloud can store and return multiple agents for one site
- one site has at most one active leader record at a given epoch
- failover events are queryable by site

### MA-1.2: Registration flow extension

**Components:** Android registration, desktop registration, cloud registration endpoint
**Prereqs:** MA-1.1
**Estimated effort:** 1 sprint

**Detailed instructions:**

1. Update Android registration to send device class, peer endpoint metadata, and capabilities.
2. Add equivalent desktop registration behavior.
3. Store per-device priority and role capability in the cloud-issued config snapshot.
4. Ensure re-registration preserves agent identity and does not accidentally create duplicate active devices for the same installation.

**Acceptance criteria:**

- Android and desktop devices register into the same site-agent registry model
- cloud returns a config snapshot that includes HA settings and peer directory bootstrap data

### MA-1.3: Basic operational visibility

**Components:** Cloud dashboard, diagnostics
**Prereqs:** MA-1.1
**Estimated effort:** 4 days

**Detailed instructions:**

1. Show all agents for a site with:
   - device class
   - current role
   - last heartbeat
   - last replication lag
   - app version
2. Highlight the current primary and current epoch.
3. Show recent failover events and stale-writer rejections.

**Acceptance criteria:**

- operators can identify which agent is primary without device-level inspection
- stale or missing peers are visible from the cloud dashboard

## 9. Phase 2 - Peer Connectivity and Discovery (Sprints 4-6)

### Goal

Allow agents on the site LAN to discover each other, authenticate, and exchange health reliably.

### MA-2.1: Peer API contract

**Components:** Shared contracts, Android agent, desktop agent
**Prereqs:** MA-0.2
**Estimated effort:** 4 days

**Detailed instructions:**

1. Define peer endpoints such as:
   - `GET /peer/health`
   - `POST /peer/heartbeat`
   - `GET /peer/bootstrap`
   - `GET /peer/sync?since={seq}`
   - `POST /peer/claim-leadership`
   - `POST /peer/proxy/preauth`
   - `GET /peer/proxy/pump-status`
2. Define signed request headers or certificate-based peer auth.
3. Define peer response payloads with:
   - role
   - epoch
   - config version
   - replication lag
   - last sequence applied
   - `senderUtcTime` (ISO 8601) — sender's local UTC clock at time of response
   - `peerClockOffsetMs` (long) — receiver-computed clock offset (see MA-2.4)

**Acceptance criteria:**

- Android and desktop teams have one shared peer API contract
- peer auth and request signing rules are explicit

### MA-2.2: Desktop peer runtime

**Components:** Desktop Edge Agent
**Prereqs:** MA-2.1
**Estimated effort:** 1 sprint

**Detailed instructions:**

1. Add peer API hosting to the desktop agent.
2. Add peer directory caching and refresh logic.
3. Add heartbeat sender and receiver behavior.
4. Add diagnostics showing:
   - visible peers
   - current leader
   - role eligibility
   - connectivity status

**Acceptance criteria:**

- desktop agent can advertise itself and receive peer health traffic
- peer connectivity survives app restart and config refresh

### MA-2.3: Android peer runtime

**Components:** Android Edge Agent
**Prereqs:** MA-2.1
**Estimated effort:** 1 sprint

**Detailed instructions:**

1. Add peer client and peer API server capability inside the foreground service.
2. Keep peer traffic coalesced into the same cadence controller used by the always-on runtime.
3. Support peer API exposure only when site HA is enabled and LAN access is configured.
4. Expose local diagnostics for current peer visibility and heartbeat health.

**Acceptance criteria:**

- Android agents can discover and communicate with peers on the site LAN
- peer coordination does not create independent hot loops outside the cadence controller

### MA-2.4: Clock synchronization approach (P2-19)

**Components:** Shared contracts, Android agent, desktop agent
**Prereqs:** MA-2.1
**Estimated effort:** 1 day (design only)

The HA system must work correctly without NTP. Internet outages lasting hours or days are common at African fuel stations, and wall-clock time cannot be assumed synchronized across agents. All time-dependent mechanisms use relative/monotonic measurements instead.

**Design constraints:**

1. **Replication ordering:** All replication uses monotonic sequence numbers assigned by the primary, not wall-clock timestamps. Standbys apply changes strictly in sequence order. The `replicationSequence` field in MA-3.1 is the authoritative ordering key — `lastModifiedUtc` is informational only and must never be used for conflict resolution or ordering.

2. **Failure detection:** Heartbeat failure detection uses elapsed time since the last successful heartbeat response (monotonic clock / stopwatch), not absolute UTC timestamps. The `failoverTimeoutSeconds` threshold is evaluated against the local monotonic measurement of time since the last successful round-trip, not by comparing wall-clock values between agents.

3. **Clock skew detection:** The `POST /peer/heartbeat` response includes a `peerClockOffsetMs` field. Each agent includes its local UTC time in the heartbeat; the receiver computes `peerClockOffsetMs = receiverUtcNow - senderReportedUtc` and returns it. If the absolute offset exceeds 30 seconds, the receiver logs a diagnostic warning: `"Peer clock skew exceeds 30s (offset={offsetMs}ms, peer={agentId})"`. Clock skew warnings are surfaced in the peer diagnostics panel but do not block any HA operations.

4. **No NTP dependency:** No HA correctness property (election, replication ordering, fencing, failover detection) depends on wall clocks being synchronized. UTC timestamps are recorded for human-readable audit logs and diagnostics only.

**Acceptance criteria:**

- replication uses sequence numbers, not wall-clock timestamps, for ordering
- heartbeat failure detection uses elapsed time, not absolute timestamps
- peer clock skew is detectable via `peerClockOffsetMs` and logged when excessive
- no correctness dependency on NTP or synchronized wall clocks

---

### MA-2.5: Localhost facade contract definition (P2-20)

**Components:** Shared contracts, Android agent, desktop agent
**Prereqs:** MA-2.1
**Estimated effort:** 2 days (contract definition only — implementation remains in Phase 4)

The proxy contract is defined alongside the peer API so that MA-2.1 peer endpoints include the correct proxy payloads from the start. Implementation of the proxy behavior is deferred to MA-4.1.

**Contract definition:**

1. **Proxied endpoints** (standby forwards to primary via peer API):
   - `POST /peer/proxy/preauth` — proxies the local `POST /api/preauth` request to the current primary for execution. Payload mirrors the local pre-auth request body. Response mirrors the primary's pre-auth response.
   - `GET /peer/proxy/pump-status` — proxies the local `GET /api/pump-status` request to the current primary for live FCC state. Response mirrors the primary's pump-status response.

2. **Locally served endpoints** (standby serves from replicated cache):
   - `GET /api/transactions` — served from the local replicated transaction buffer when the replication lag is within `maxReplicationLagSeconds`. If replication lag exceeds the threshold, the standby proxies to primary instead.

3. **Freshness threshold:** The standby uses its current replication lag (measured as `primarySequence - localSequence`) to decide cache-vs-proxy. When the lag is within the configured `maxReplicationLagSeconds`, the local cache is considered fresh. Otherwise, the request is proxied to the primary.

4. **Error behavior when primary is unreachable during proxy:**
   - Retry up to 2 times with 500ms delay between attempts.
   - If all retries fail, return HTTP 503 with error body: `{ "error": "PRIMARY_UNREACHABLE", "message": "The current primary agent is not reachable. Retry shortly or check site connectivity." }`
   - The standby must not attempt to serve authoritative data (pre-auth, live pump status) from its local cache as a fallback — these require the primary.

**Acceptance criteria:**

- proxy contract is defined in Phase 2 alongside the peer API
- peer API endpoints (`/peer/proxy/preauth`, `/peer/proxy/pump-status`) include proxy payloads
- Phase 4 MA-4.1 implementation references this contract

---

## 10. Phase 3 - Replication and Standby Readiness (Sprints 6-8)

### Goal

Keep standby nodes close enough to primary state that promotion is safe.

### MA-3.1: Replication data model

**Components:** Android storage, desktop storage, cloud audit model
**Prereqs:** MA-2.1
**Estimated effort:** 4 days

**Detailed instructions:**

1. Define the replicated state set:
   - buffered transactions
   - pre-auth queue and status
   - `SYNCED_TO_ODOO` state
   - ingestion cursor
   - nozzle mappings
   - config version
   - leadership epoch
   - replication sequence
2. Add per-record metadata needed for replication:
   - source agent
   - sequence number (monotonic, assigned by primary — this is the authoritative ordering key per MA-2.4; not wall-clock timestamps)
   - last modified UTC (informational only — must not be used for ordering or conflict resolution)
3. Define snapshot and delta payload formats.

**Acceptance criteria:**

- both agents can persist and reason about replication sequence and lag
- snapshot and delta schemas cover all promotion-critical state

### MA-3.2: Snapshot bootstrap and delta sync

**Components:** Android agent, desktop agent
**Prereqs:** MA-3.1, MA-2.2, MA-2.3
**Estimated effort:** 1 sprint

**Detailed instructions:**

1. Implement full snapshot bootstrap for a new or recovering standby.
2. Implement delta sync from a monotonic sequence.
3. Make snapshot application atomic so a standby never promotes from a partial restore.
4. Add recovery checkpoints to survive app restart during sync.

**Acceptance criteria:**

- a standby can build a full warm state from the primary
- delta sync resumes from the last applied sequence after restart

### MA-3.3: Standby readiness gates

**Components:** Android agent, desktop agent, cloud visibility
**Prereqs:** MA-3.2
**Estimated effort:** 4 days

**Detailed instructions:**

1. Compute replication lag in seconds and last sequence distance.
2. Mark standby readiness states:
   - `HOT`
   - `CATCHING_UP`
   - `BLOCKED`
3. Prevent automatic promotion when:
   - lag exceeds threshold
   - snapshot is incomplete
   - config version is stale
4. Publish readiness and lag telemetry to the cloud.

**Acceptance criteria:**

- agents can prove whether a standby is promotable
- promotion gating is observable in diagnostics and cloud dashboards

## 11. Phase 4 - Localhost Facade and Planned Switchover (Sprints 8-9)

### Goal

Preserve the Android localhost contract while allowing leadership to live on a different device.

### MA-4.1: Android localhost facade

**Components:** Android agent, Odoo integration surface
**Prereqs:** MA-2.3, MA-2.5, MA-3.2
**Estimated effort:** 1 sprint

> **Note:** The proxy contract (which endpoints to proxy, which to serve from cache, freshness thresholds, and error behavior) is defined in **MA-2.5** (Phase 2). This task implements that contract.

**Detailed instructions:**

1. Keep all Odoo-facing APIs on localhost unchanged.
2. Implement the proxy/cache behavior defined in the MA-2.5 contract:
   - proxy `POST /api/preauth` to primary via `POST /peer/proxy/preauth`
   - proxy live `GET /api/pump-status` to primary via `GET /peer/proxy/pump-status`
   - serve `GET /api/transactions` from local replicated cache when replication lag is within `maxReplicationLagSeconds`, else proxy to primary
   - apply the MA-2.5 error behavior (retry policy, HTTP 503 `PRIMARY_UNREACHABLE`) when primary is unreachable
3. Add request correlation IDs so proxied calls are traceable end to end.

**Acceptance criteria:**

- Odoo continues to use localhost on every HHT
- proxied requests are observable and traceable
- no manual primary IP entry is required in normal operation

### MA-4.2: Planned switchover controls

**Components:** Desktop agent, cloud dashboard, Android diagnostics
**Prereqs:** MA-3.3
**Estimated effort:** 4 days

**Detailed instructions:**

1. Add a supervisor-triggered switchover command.
2. Implement switchover choreography:
   - drain in-flight work
   - flush final replication checkpoint
   - claim next epoch on target standby
   - demote old primary
3. Record each step in the audit trail.
4. Expose the command only when the target standby is promotable.

**Acceptance criteria:**

- leadership can be moved deliberately without data loss
- switchover is blocked when target standby is not ready

### MA-4.3: Compatibility regression suite

**Components:** Android tests, desktop tests, integration tests
**Prereqs:** MA-4.1
**Estimated effort:** 3 days

**Detailed instructions:**

1. Validate all Odoo-facing localhost endpoints under:
   - local primary
   - remote desktop primary
   - remote Android primary
2. Validate that response shapes, error shapes, and timing remain within acceptable bounds.

**Acceptance criteria:**

- no contract regression is introduced for Odoo POS

## 12. Phase 5 - Automatic Failover and Recovery (Sprints 9-11)

### Goal

Automate suspect detection, election, promotion, demotion, and rejoin without allowing split brain.

### MA-5.1: Heartbeat and suspect detection

**Components:** Android agent, desktop agent
**Prereqs:** MA-2.2, MA-2.3
**Estimated effort:** 4 days

**Detailed instructions:**

1. Implement peer heartbeat cadence at the configured interval.
2. Mark a primary as suspected after configurable missed-heartbeat thresholds.
3. Require a direct health probe failure before election begins.
4. Record suspicion, health-probe, and recovery events in local audit logs.

**Acceptance criteria:**

- primary suspicion uses a deterministic and configurable threshold
- transient packet loss does not immediately trigger election

### MA-5.1a: Election Algorithm (P2-14)

The following explicit algorithm governs leader election for small clusters (2-3 agents). It is designed to be deterministic, priority-aware, and resilient to internet outages.

#### Algorithm Steps

1. **Standby detects primary failure:**
   - Missed heartbeats exceed `failoverTimeoutSeconds` threshold (default: 30s at 5s intervals = 6 missed heartbeats).
   - Direct health probe to primary fails (POST `/peer/health` returns error or times out within 3s).
   - Both conditions must be met before entering CANDIDATE state.

2. **Enter CANDIDATE state:**
   - Agent transitions from `STANDBY_HOT` to `CANDIDATE`.
   - FCC communication remains paused (CANDIDATE cannot issue FCC commands or serve authoritative writes).
   - A CANDIDATE that observes a higher-priority CANDIDATE or a valid PRIMARY immediately reverts to `STANDBY_HOT`.

3. **Pre-election checks:**
   - Verify no higher-priority healthy peer exists: send a health probe to all known peers. If any peer with a lower priority value (higher priority) responds as `HEALTHY`, revert to `STANDBY_HOT` and let that peer claim leadership.
   - Verify own replication lag is within `maxReplicationLagSeconds`. If lag exceeds the threshold, promotion is blocked (enter `RECOVERING` instead).
   - Verify own config version matches or exceeds the primary's last known config version.

4. **Priority-based back-off wait:**
   - Base delay = `(1000 - priority) * 10ms`. A priority of 10 waits 9,900ms; a priority of 100 waits 9,000ms. This ensures higher-priority agents (lower priority number) have shorter waits.
   - Add random jitter: `0-500ms` to break exact ties.
   - During this wait, if a higher-priority leadership claim is received, revert to `STANDBY_HOT` immediately.

5. **Broadcast leadership claim:**
   - Compute `newEpoch = max(all locally seen epochs) + 1`.
   - Sign the claim with the site's peer shared secret (HMAC-SHA256).
   - Send to all known peers via `POST /peer/claim-leadership` containing:
     ```json
     {
       "agentId": "device-abc-123",
       "newEpoch": 5,
       "priority": 10,
       "configVersion": 42,
       "hmac": "base64-signature"
     }
     ```

6. **Wait `electionTimeoutSeconds` (default: 10s) for responses:**
   - If a higher-priority claim is received (lower priority number, same or higher epoch): revert to `STANDBY_HOT`.
   - If a rejection is received (peer sees a valid higher-epoch leader): revert to `STANDBY_HOT`.
   - If a `NACK` with a higher epoch is received: update local epoch, revert to `STANDBY_HOT`.
   - If no objection within timeout: assume leadership.

7. **On assuming leadership:**
   - Transition to `PRIMARY` state.
   - Persist the new epoch locally (to survive reboots).
   - Open FCC session and begin authoritative operations.
   - Begin publishing heartbeats as PRIMARY.
   - Report the new epoch to cloud on next successful contact (cloud is a passive observer, see P2-15).

8. **If cloud is unreachable during election:**
   - Election proceeds purely peer-to-peer using the steps above.
   - The new epoch is registered with the cloud on next successful contact.
   - Cloud accepts any epoch strictly greater than its current record (Option B — agent elections are authoritative).

#### Quorum Behavior

**2-agent cluster:**
- No quorum requirement. If the standby cannot reach the primary, it promotes itself after the back-off period.
- Split-brain is mitigated by epoch fencing: the cloud rejects writes from the stale-epoch agent.
- On reconvergence, the lower-epoch agent self-demotes upon receiving a heartbeat with a higher epoch.

**3-agent cluster:**
- Priority ordering prevents simultaneous claims in most cases.
- If two agents enter CANDIDATE simultaneously (e.g., both detect primary failure at the same time), the back-off timer ensures the higher-priority agent claims first.
- In the rare case of exactly simultaneous claims with different epochs, the higher epoch wins. With the same epoch, the lower priority value (higher priority) wins.
- A third agent observing two competing claims accepts the claim with the highest epoch, breaking ties by priority.

#### Tie-Breaking Rules

1. Higher epoch always wins.
2. Same epoch: lower priority value wins (lower number = higher priority).
3. Same epoch and priority: earlier `RegisteredAt` timestamp wins (deterministic from cloud registry).

#### Test Matrix Reference

The scenario "Two agents boot at the same time" follows this algorithm: both enter CANDIDATE, the back-off timer ensures the higher-priority agent claims first, and the lower-priority agent reverts to STANDBY_HOT upon observing the claim.

#### Cold-Start Election Behavior (P2-24)

When no prior epoch exists for a site (first deployment or after a full reset), the standard election algorithm above is supplemented by the following cold-start rules:

**Scenario 1: Single agent boots, no prior epoch**

1. On first boot, agent checks with cloud for current leadership (`GET /api/v1/agent/config` returns peer directory with leader info).
2. Cloud returns no leader (epoch 0, no `HaLeaderAgentId` recorded).
3. Agent claims epoch 1 and transitions to `PRIMARY`.
4. Reports epoch 1 to cloud on next successful contact. Cloud records this agent as leader.

**Scenario 2: Two agents boot simultaneously, no prior epoch (epoch 0)**

1. Both agents query the cloud on first boot and see epoch 0 with no leader.
2. Both attempt to claim epoch 1 and report to the cloud.
3. Cloud accepts the **first** write with epoch 1 and records that agent as `HaLeaderAgentId`.
4. The second agent's write is rejected (`409 CONFLICT.NON_LEADER_WRITE` — same epoch, different agent).
5. The rejected agent fetches the updated config and sees the recorded leader. It falls back to `STANDBY_HOT` and adopts the leader's epoch.
6. On the LAN, if both agents broadcast claims simultaneously, the standard tie-breaking rules apply (priority, then `RegisteredAt`).

**Scenario 3: Agent boots with cloud unreachable, no prior epoch**

1. Agent cannot reach the cloud to check for existing leadership.
2. Agent uses priority-based tie-breaking with randomized back-off (same as step 4 of the main algorithm).
3. If no peer is reachable on the LAN, agent claims epoch 1 and transitions to `PRIMARY`.
4. If a peer is reachable and also claims epoch 1, standard tie-breaking rules apply (priority, then `RegisteredAt`).
5. On first successful cloud contact, agent registers its epoch. Cloud accepts it if no higher epoch exists.

**Scenario 4: Agent boots and cloud returns an existing leader**

1. Agent queries the cloud and receives a peer directory with a valid leader (epoch > 0, `HaLeaderAgentId` set).
2. Agent adopts that leader and enters the appropriate role:
   - If the agent **is** the recorded leader: resume `PRIMARY`.
   - If the agent is **not** the recorded leader: enter `STANDBY_HOT` (or `RECOVERING` if replication lag is unknown).
3. Normal heartbeat monitoring begins. If the recorded leader is unreachable, the standard election algorithm applies.

**Invariant:** At most one agent transitions to `PRIMARY` during cold start. Cloud epoch fencing (P2-15) is the ultimate arbiter — even if two agents both believe they are primary with epoch 1, the cloud accepts the first and rejects the second, forcing convergence.

### MA-5.1b: Cloud Role During Elections — Option B (P2-15)

The cloud is a **passive observer** of elections, not an arbiter. Agent-side elections are authoritative. The cloud records the result and enforces it for write fencing.

#### Epoch-Based Write Fencing Rules

When an agent sends an authoritative write (transaction upload, pre-auth forward) with a `leaderEpoch` header:

| Condition | Action |
|---|---|
| `requestEpoch > siteMaxEpoch` | Accept the write. Update the site's recorded leader (`HaLeaderAgentId`) and epoch (`HaLeaderEpoch`). This agent won a new election. |
| `requestEpoch == siteMaxEpoch` AND `requestDevice == recordedLeader` | Accept the write. This is the current known leader. |
| `requestEpoch == siteMaxEpoch` AND `requestDevice != recordedLeader` | Reject with `409 CONFLICT.NON_LEADER_WRITE`. This agent is a stale writer at the same epoch. |
| `requestEpoch < siteMaxEpoch` | Reject with `409 CONFLICT.STALE_LEADER_EPOCH`. This agent is operating with an outdated epoch. |

#### Cloud-Unreachable Elections

When agents conduct an election while the cloud is unreachable:
1. The election completes purely peer-to-peer using the algorithm in MA-5.1a.
2. The winning agent persists the new epoch locally.
3. On next successful cloud contact, the agent includes the new epoch in its request headers.
4. The cloud sees `requestEpoch > siteMaxEpoch`, accepts the write, and records the new leader/epoch.
5. Subsequent config polls reflect the new leader in the peer directory.

#### SiteHaLeadershipResolver Behavior

The `SiteHaLeadershipResolver` uses the **recorded** leader and epoch from `Site.HaLeaderAgentId` / `Site.HaLeaderEpoch` when available. Priority-based leader computation is only used as a fallback for cold start (no epoch has been recorded yet). This ensures the config-delivered peer directory reflects the agent-elected leader, not a cloud-computed one.

### MA-5.2: Election and epoch fencing

**Components:** Android agent, desktop agent, cloud backend
**Prereqs:** MA-5.1, MA-3.3
**Estimated effort:** 1 sprint
**Current status (2026-03-15):** Partially implemented. Cloud stale-writer rejection is live with P2-15 epoch-based fencing (agent elections authoritative). Peer election and self-demotion are still pending.

**Detailed instructions:**

1. Implement priority-based election with epochs.
2. Persist the latest seen epoch locally so reboots do not reuse stale leadership.
3. Require each candidate to:
   - verify no higher-priority healthy peer exists
   - increment epoch
   - broadcast a signed claim
   - open FCC ownership only after claim succeeds
4. Implement immediate self-demotion when a node observes a higher valid epoch.
5. Update cloud authoritative-write endpoints to reject stale epochs.

**Acceptance criteria:**

- only one primary remains after election settles
- stale leader writes are rejected by the cloud
- FCC ownership is never intentionally shared by two agents

### MA-5.3: Recovery and rejoin

**Components:** Android agent, desktop agent
**Prereqs:** MA-5.2
**Estimated effort:** 4 days

**Detailed instructions:**

1. On restart or rejoin, force the returning node into `RECOVERING`.
2. Block automatic promotion until:
   - snapshot and delta catch-up complete
   - config version matches
   - lag is within threshold
3. Do not auto-preempt the current primary even if the returning node has a higher configured priority.
4. Add explicit operator tooling for planned failback if ever needed.

**Acceptance criteria:**

- a recovered former primary rejoins safely as standby
- no leader flapping occurs during repeated restart tests

### MA-5.4: In-Flight Pre-Auth Handling During Failover (P2-17)

**Components:** Android agent, desktop agent
**Prereqs:** MA-3.2, MA-5.2
**Estimated effort:** 4 days
**Resolves:** GAP-4 (§19.1)
**Reference:** `docs/specs/research/fcc-session-handover.md`

Pre-auths in non-terminal states (PENDING, AUTHORIZED, DISPENSING) require special handling during failover. The standard delta-sync interval is not fast enough — pre-auths change state rapidly and a promotion with stale pre-auth state risks double-authorization or orphaned dispenses.

#### 5.4.1 Replication of pre-auth state

Pre-auth state changes must be replicated to standby in near-real-time via the peer heartbeat (piggybacked as a lightweight delta). Each heartbeat carries:

- List of active (non-terminal) pre-auth IDs and their current states
- Any pre-auth state changes since last heartbeat (state, timestamps, FCC correlation data)
- Total active pre-auth count for quick sanity check

This piggyback is in addition to the standard delta-sync (MA-3.2). The delta-sync provides full record transfer; the heartbeat piggyback provides rapid state freshness for the small number of active pre-auths (typically 0-5 per site).

**Heartbeat payload extension:**

```json
{
  "agentId": "device-abc-123",
  "currentRole": "PRIMARY",
  "leaderEpoch": 5,
  "activePreAuths": [
    {
      "id": "pa-uuid-1",
      "pumpNumber": 3,
      "status": "AUTHORIZED",
      "fccCorrelationId": "DOMS-12345",
      "authorizedAt": "2026-03-15T10:30:00Z",
      "requestedAmount": 5000.00
    }
  ],
  "preAuthChangesSinceLastHb": [
    {
      "id": "pa-uuid-2",
      "previousStatus": "PENDING",
      "newStatus": "AUTHORIZED",
      "changedAt": "2026-03-15T10:30:05Z"
    }
  ]
}
```

**Implementation notes:**

- Heartbeat interval is 5s (default), so pre-auth state freshness is ≤5s on the standby
- Piggyback payload is small (active pre-auths are typically single-digit count)
- Standby applies heartbeat pre-auth updates to its local `BufferedPreAuth` records immediately
- If heartbeat pre-auth data conflicts with a subsequent delta-sync record, the delta-sync record wins (it has full field coverage)

#### 5.4.2 On promotion — new primary adopts pre-auths

When a standby is promoted to PRIMARY, it must adopt all non-terminal pre-auths from its replicated state. The adoption behavior depends on the pre-auth state at the time of promotion:

| Pre-Auth State at Promotion | New Primary Action | Rationale |
|---|---|---|
| **PENDING** (sent to FCC, no response yet) | Mark as `UNKNOWN`. Do **not** re-send to FCC. Wait for FCC to either report a dispense or for the pre-auth to expire (`ExpiresAt`). | Re-sending risks double-authorization. The FCC may have already authorized the pump. The safe action is to wait and observe. |
| **AUTHORIZED** (pump authorized, not yet dispensing) | Monitor FCC for dispense-start and dispense-complete events. Match incoming events to the replicated pre-auth via `FccCorrelationId` or pump number. | The pump is live. Fuel may begin flowing at any time. The new primary must be ready to capture the dispense-complete. |
| **DISPENSING** (fuel is flowing) | Monitor FCC for dispense-complete event. Match to replicated pre-auth. | Fuel is physically flowing. The dispense-complete event will arrive from the FCC regardless of which agent is primary. |

**UNKNOWN state handling:**

The `UNKNOWN` state is a new terminal-pending state introduced for failover scenarios:

- An UNKNOWN pre-auth is **not** re-sent to the FCC
- The new primary monitors FCC events (transaction completion, pump status changes) for evidence that the pre-auth was processed
- If a matching FCC transaction appears → transition to COMPLETED and reconcile
- If `ExpiresAt` passes with no FCC activity on that pump → transition to EXPIRED
- If the FCC reports the pump is IDLE with no transaction → transition to EXPIRED
- UNKNOWN pre-auths are reported to the cloud with a `failoverOrphaned: true` flag for operator visibility

**Adoption sequence (executed immediately after election win, before publishing PRIMARY heartbeats):**

```
1. Query local BufferedPreAuth WHERE Status IN (PENDING, AUTHORIZED, DISPENSING)
2. For each PENDING record:
   a. Set Status = UNKNOWN
   b. Set SourceAgentId = self (new primary now owns it)
   c. Log: "Pre-auth {id} adopted as UNKNOWN after failover"
3. For each AUTHORIZED or DISPENSING record:
   a. Set SourceAgentId = self
   b. Retain current status
   c. Register pump number in FCC event watch list
   d. Log: "Pre-auth {id} adopted in state {status} after failover"
4. Open FCC session (per vendor — see fcc-session-handover.md)
5. Begin monitoring FCC events for adopted pre-auth pumps
```

**Vendor-specific considerations:**

- **DOMS:** New primary connects and subscribes to pump status events. Supervised transaction buffer may contain the completed transaction for an adopted pre-auth.
- **Petronite:** `ReconcileOnStartupAsync()` fetches pending orders from Petronite cloud — cross-reference with replicated pre-auths for double coverage.
- **Radix/Advatec:** Stateless polling. New primary polls for completed transactions and matches to adopted pre-auths.

#### 5.4.3 Test scenarios (additions to §15)

| Scenario | Expected Result |
|---|---|
| Primary fails with PENDING pre-auth (no FCC response yet) | New primary marks pre-auth UNKNOWN; FCC either completes (matched on dispense-complete) or times out (marked EXPIRED at `ExpiresAt`) |
| Primary fails with AUTHORIZED pre-auth (pump is live) | New primary monitors FCC for dispense-complete; matches to replicated pre-auth via `FccCorrelationId` or pump number; transaction captured |
| Primary fails with DISPENSING pre-auth (fuel flowing) | New primary captures dispense-complete from FCC and reconciles with adopted pre-auth; no data loss |
| Primary fails with PENDING pre-auth, FCC authorized the pump before failure was detected | New primary (state: UNKNOWN) observes dispense-complete from FCC; transitions UNKNOWN → COMPLETED; no double-authorization |
| Primary fails with pre-auth, standby has stale heartbeat (missed last 2 heartbeats) | New primary adopts pre-auth from last-known state; max staleness = 2 × heartbeat interval (10s); any state drift is resolved by FCC event observation |

### MA-5.5: Ingestion Mode × Failover Interaction (P2-18)

**Components:** Android agent, desktop agent
**Prereqs:** MA-5.2
**Estimated effort:** 2 days
**Resolves:** GAP-5 (§19.1)

Failover behavior differs depending on the site's configured ingestion mode (REQ-12). This section documents the interaction and any limitations.

#### 5.5.1 CLOUD_DIRECT (default)

**Behavior:** Failover is transparent to the FCC.

- The FCC pushes transactions directly to the cloud. The cloud ingestion endpoint is stable regardless of which agent is primary.
- The primary agent's role in CLOUD_DIRECT is safety-net LAN catch-up polling and pre-auth execution. On failover:
  - New primary takes over LAN catch-up polling from the failed primary
  - New primary adopts in-flight pre-auths (per MA-5.4)
  - No FCC reconfiguration is needed
- Transaction flow is uninterrupted because the FCC's push target (cloud URL) does not change.

**Risk:** Minimal. CLOUD_DIRECT is the safest mode for multi-agent failover.

#### 5.5.2 RELAY

**Behavior:** Degraded failover. FCC push is interrupted; LAN catch-up poll by new primary handles the gap.

- In RELAY mode the FCC is configured to push transactions to the primary agent's LAN IP address.
- On failover, the FCC's push target (old primary's IP) becomes unreachable.

**Options evaluated:**

| Option | Description | Verdict |
|---|---|---|
| (a) Shared VIP / floating IP | Both agents share a virtual IP on the LAN; new primary claims it | Requires LAN infrastructure changes (VRRP/keepalived); not feasible on typical site WiFi/switch hardware |
| (b) FCC manual reconfiguration | Site supervisor reconfigures FCC to push to new primary's IP | Defeats purpose of automatic failover; unacceptable for unattended operation |
| (c) Accept push interruption | FCC push fails until old primary recovers or is reconfigured; new primary uses LAN catch-up polling to cover the gap | Simple; no infrastructure changes; catch-up poll ensures no data loss |

**Recommendation:** Option (c) for MVP.

- FCC push to the old primary's IP will fail with connection refused or timeout
- The new primary immediately begins LAN catch-up polling at the normal cadence
- Transactions that occurred during the gap are captured via catch-up poll (FCC retains completed transactions in its buffer)
- Pre-auth handling is unaffected (pre-auths are always LAN-initiated by the agent, not pushed by FCC)

**Documented limitation:** RELAY mode has degraded failover compared to CLOUD_DIRECT. During the gap between primary failure and catch-up poll, transactions are buffered on the FCC (not lost) but cloud visibility is delayed. Gap duration = suspect detection time + election time + first catch-up poll cycle.

**Operator guidance:** Sites requiring seamless failover should prefer CLOUD_DIRECT mode. RELAY mode is suitable when internet connectivity is unreliable and local buffering is preferred, but operators should understand the failover trade-off.

#### 5.5.3 BUFFER_ALWAYS

**Behavior:** Stranded buffer on failed primary contains un-synced transactions.

- In BUFFER_ALWAYS mode, the primary agent always buffers transactions locally before uploading to the cloud.
- On failover, any un-synced records in the failed primary's local buffer are stranded until the device recovers.

**Risk quantification:**

```
Stranded records = sync_interval × transaction_rate

Example (busy station):
  sync_interval = 30s (CloudUploadWorker default)
  transaction_rate = 2 transactions/minute (peak)
  → max stranded = 30s × (2/60) = 1 transaction

Example (high-volume station):
  sync_interval = 30s
  transaction_rate = 10 transactions/minute (peak)
  → max stranded = 30s × (10/60) = 5 transactions
```

**With replication (Phase 3):**

```
Stranded records = replication_lag × transaction_rate

With heartbeat-piggybacked pre-auths (MA-5.4):
  replication_lag ≈ 5s (one heartbeat interval)
  transaction_rate = 10/min
  → max stranded = 5s × (10/60) ≈ 0.8 transactions ≈ 1 transaction
```

**Mitigation strategy:**

1. **Phase 3 replication** reduces the risk window from `sync_interval` to `replication_lag` (5s vs 30s)
2. When the failed device recovers (enters RECOVERING state), its `CloudUploadWorker` resumes and uploads the stranded buffer records
3. Cloud deduplication (idempotency key on transaction ID) prevents duplicates if the same record was both replicated to the new primary and later uploaded by the recovered device
4. The stranded buffer count is logged as a metric for operator visibility

**Acceptable risk threshold:** ≤5 stranded transactions at peak volume with replication enabled. Without replication, ≤10 stranded transactions. Both are acceptable given that the records are preserved on disk and will be uploaded upon device recovery.

**Operator guidance:** BUFFER_ALWAYS sites should enable replication (Phase 3) before enabling automatic failover (Phase 5) to minimize the stranded buffer window. The `maxReplicationLagSeconds` promotion gate (MA-3.3) ensures the standby is fresh enough that stranded records are minimal.

## 13. Phase 6 - Hardening and Rollout (Sprints 11-12)

### Goal

Prove the design under realistic failures and make it operable for field rollout.

### MA-6.1: Chaos and soak validation

**Components:** Virtual lab, Android, desktop, cloud
**Prereqs:** MA-5.3
**Estimated effort:** 1 sprint

**Detailed instructions:**

1. Run repeated fault drills:
   - kill primary process
   - reboot primary device
   - disconnect internet while keeping LAN up
   - partition one standby from the leader
   - restore a stale former primary
2. Run soak tests with sustained transaction traffic and repeated leadership changes.
3. Validate:
   - zero transaction loss
   - zero duplicate pre-auth execution
   - bounded failover time

**Acceptance criteria:**

- failover remains within the target window during repeated drills
- no data-loss regression is found in soak tests

### MA-6.2: Operations and runbooks

**Components:** Runbooks, portal/dashboard, diagnostics
**Prereqs:** MA-6.1
**Estimated effort:** 4 days

**Detailed instructions:**

1. Create runbooks for:
   - forced switchover
   - stale-primary recovery
   - standby-not-ready investigation
   - split-brain alarm response
2. Add alerting rules (P2-23). Alerts must be deliverable via configurable channels (webhook, email, dashboard notification). The following rules are required:

   | Alert | Severity | Trigger | Recipients |
   |---|---|---|---|
   | Failover occurred | CRITICAL | New leader epoch detected for site (epoch increased, `HaLeaderAgentId` changed) | Ops Manager, Site Supervisor |
   | No promotable standby | CRITICAL | Site has only one active agent, or all standbys have `lastReplicationLagSeconds > maxReplicationLagSeconds` | Ops Manager |
   | Replication lag high | WARNING | Any standby's `lastReplicationLagSeconds > maxReplicationLagSeconds * 0.8` | Ops Manager |
   | Election flapping | WARNING | More than 2 leader epoch changes within 10 minutes for a single site | Ops Manager |
   | Stale-writer rejection | CRITICAL | `AuthoritativeWriteFenceService` rejects a write (`409 CONFLICT.STALE_LEADER_EPOCH` or `409 CONFLICT.NON_LEADER_WRITE`) | Ops Manager |
   | Peer directory stale | WARNING | Agent's `peerDirectoryVersion` is behind cloud by more than 5 minutes (computed from last config poll carrying the current version) | Ops team |

   Alert delivery configuration should be per-site, stored in the site configuration, with a default channel (e.g., webhook URL) and per-alert severity overrides.

3. Add operator-visible role and failover history screens.

**Acceptance criteria:**

- site support staff can identify the current leader and reason about failover state
- operational response steps exist for all critical failure modes
- alert rules are documented with severity, trigger condition, and recipient list
- alerts can be delivered via at least two configurable channels (webhook and email or dashboard)

### MA-6.3: Pilot rollout

**Components:** Release management, cloud, Android, desktop
**Prereqs:** MA-6.2
**Estimated effort:** 1 sprint

**Detailed instructions:**

1. Pilot on a small number of representative sites:
   - desktop-preferred site
   - Android-only fallback site
   - unstable-connectivity site
2. Roll out with feature flags:
   - peer discovery
   - replication
   - planned switchover
   - automatic failover
3. Review failover events and operational burden before wider rollout.

**Acceptance criteria:**

- pilot sites complete without unresolved split-brain or transaction-loss incidents
- rollout can be halted or rolled back by feature flag

## 14. Cross-Phase Dependency Map

| Blocking Item | Unlocks |
|---|---|
| MA-0.2 Control-plane contract definition | MA-1.1, MA-1.2, MA-2.1 |
| MA-1.1 Cloud site-agent registry | MA-1.2, MA-1.3, MA-5.2 |
| MA-2.1 Peer API contract | MA-2.2, MA-2.3, MA-3.2 |
| MA-3.2 Snapshot bootstrap and delta sync | MA-3.3, MA-4.1, MA-5.2 |
| MA-3.3 Standby readiness gates | MA-4.2, MA-5.2 |
| MA-4.1 Android localhost facade | MA-4.3, pilot validation |
| MA-5.2 Election and epoch fencing | MA-5.3, MA-6.1 |

## 15. Test Matrix

| Scenario | Expected Result |
|---|---|
| Desktop primary process crash | Highest-priority healthy standby promotes automatically |
| Android primary battery dies | Desktop or next Android standby promotes automatically |
| Primary app restart | Recovered node rejoins as standby after catch-up |
| LAN up, internet down | Local election still works and site continues operating |
| Internet up, LAN partition | Higher epoch wins after reconvergence; stale writes rejected |
| Two agents boot at the same time | Exactly one wins leadership per MA-5.1a election algorithm: priority-based back-off ensures higher-priority agent claims first; lower-priority agent reverts to STANDBY_HOT |
| Standby behind on replication | Automatic promotion blocked or marked emergency with forced catch-up |
| Planned supervisor switchover | Leadership moves with no duplicate FCC commands |
| Former primary returns stale | Node self-demotes and re-enters `RECOVERING` |
| Primary fails with PENDING pre-auth (P2-17) | New primary marks pre-auth UNKNOWN; FCC either completes or times out |
| Primary fails with AUTHORIZED pre-auth — pump live (P2-17) | New primary monitors FCC for dispense-complete; matches to replicated pre-auth |
| Primary fails with DISPENSING pre-auth — fuel flowing (P2-17) | New primary captures dispense-complete and reconciles with adopted pre-auth |
| Cold start — single agent, no prior epoch (P2-24) | Agent claims epoch 1, becomes PRIMARY, reports to cloud |
| Cold start — two agents boot simultaneously, epoch 0 (P2-24) | Both attempt epoch 1; cloud accepts first writer, second falls back to STANDBY_HOT after 409 rejection |
| Cold start — agent boots, cloud unreachable, no prior epoch (P2-24) | Agent uses priority-based back-off, claims epoch 1 locally, registers with cloud on first successful contact |

## 16. Major Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Split brain on LAN partition | Dual primaries and duplicate commands | Epoch fencing, signed claims, self-demotion, stale-write rejection |
| FCC vendor session behavior on takeover | Failed reconnect or duplicate control attempts | Vendor-specific takeover tests and reconnect rules |
| Android background constraints | Standby may be too cold to promote safely | Foreground service, shared cadence controller, battery policy guidance |
| Replication lag | Unsafe promotion | Promotion gates, lag telemetry, emergency catch-up mode |
| Discovery failure on site WiFi | Peers cannot coordinate | Hybrid discovery using LAN plus cached peer directory from cloud |

## 17. Recommended Delivery Order

If team capacity is limited, implement in this strict order:

1. Phase 0 contracts
2. Phase 1 cloud registry and config
3. Phase 2 peer connectivity
4. Phase 3 replication
5. Phase 4 localhost facade
6. Phase 5 automatic failover
7. Phase 6 hardening and pilot

Do not enable automatic promotion before Phase 3 and Phase 4 are complete.

## 18. Summary

This plan converts the multi-agent concept into a delivery sequence that can be executed across cloud, desktop, and Android workstreams. The critical implementation principle is unchanged throughout: one active primary owns the FCC, all other agents run warm in parallel, Android preserves the localhost contract for Odoo POS, and automatic failover is enabled only after replication and fencing are in place.

---

## Emergency HA Disable and Rollback (P2-22)

This section defines the procedure for disabling multi-agent HA at a site, either as an emergency response to production issues or for gradual rollout control.

### Disable HA for a site

Set `siteHa.enabled = false` in the cloud config for the site. On the next config poll, all agents at the site:

1. **Stop peer heartbeats and replication** — heartbeat publishing and delta-sync (MA-2.2, MA-3.2) cease immediately.
2. **Stop LAN announcements and listening** — UDP broadcast and listener (§19.6, Layer 3) are suspended.
3. **The current leader remains PRIMARY** — the agent that holds the current epoch continues as the sole primary.
4. **All standbys stop standby behavior and operate independently** — each agent polls the FCC independently, each buffers transactions locally, and the cloud deduplicates uploads using its existing transaction-ID idempotency logic.

All peer traffic stops within one config poll cycle. No agent restarts are required.

### Which agent is PRIMARY after HA disable

The agent that was `PRIMARY` at the moment HA was disabled remains `PRIMARY`. Its epoch remains valid and the cloud continues to accept its authoritative writes.

**Fallback if current primary cannot be determined:**

If the cloud cannot determine the current primary (e.g., `HaLeaderEpoch` is 0 or the recorded `HaLeaderAgentId` is no longer registered), fall back to priority-based assignment:

1. Cloud selects the highest-priority active agent for the site (lowest `priority` value with `isActive = true`).
2. Assigns it epoch 1 and records it as the site leader.
3. Other agents receive this assignment on their next config poll and operate independently (no standby behavior since HA is disabled).

### Cloud dashboard emergency button

The portal exposes a **"Disable Site HA"** action on the site management page:

1. **Action:** Sets `siteHa.enabled = false` in the site configuration.
2. **Side effect:** Enqueues a `REFRESH_CONFIG` command for all agents at the site (per §19.6 Layer 1), ensuring agents pick up the change within seconds rather than waiting for the next poll cycle.
3. **Confirmation:** The portal displays a confirmation dialog explaining that all agents will operate independently and peer replication will stop.
4. **Audit:** The action is logged with the operator's identity and timestamp for operational traceability.
5. **Re-enable:** The same page provides an **"Enable Site HA"** action that sets `siteHa.enabled = true`. On re-enable, agents resume peer discovery and the normal election algorithm (MA-5.1a) determines the primary.

### Feature flag for gradual rollout

Use `siteHa.autoFailoverEnabled` independently of `siteHa.enabled` to support incremental HA rollout:

| Flag Combination | Peer Discovery | Replication | Planned Switchover | Automatic Failover |
|---|---|---|---|---|
| `siteHa.enabled = false` | No | No | No | No |
| `siteHa.enabled = true`, `autoFailoverEnabled = false` | Yes | Yes | Yes | No |
| `siteHa.enabled = true`, `autoFailoverEnabled = true` | Yes | Yes | Yes | Yes |

**HA-lite mode** (`enabled = true`, `autoFailoverEnabled = false`):

- Agents discover peers, exchange heartbeats, and replicate state.
- Standby agents maintain a warm replica and report replication lag.
- Planned switchover (MA-4.2) is available for operator-initiated role changes.
- Automatic failover (MA-5.1a election) is **disabled** — if the primary fails, the standby detects the failure and reports it to the cloud dashboard, but does not promote itself.
- The operator must use planned switchover or the emergency HA disable button to restore service.

This allows sites to validate peer discovery and replication stability before enabling fully automatic failover.

### Data safety during HA disable

When HA is disabled mid-operation:

- **In-flight replication:** Any replication delta in transit is discarded by the standby. No data loss occurs because the primary retains all authoritative data.
- **Buffered transactions on standby:** If the standby has buffered transactions (e.g., during a prior BUFFER_ALWAYS period), those buffers remain and the standby continues to upload them independently. The cloud deduplicates using transaction IDs.
- **In-flight pre-auths:** Active pre-auths continue on the primary. Standbys stop monitoring replicated pre-auth state but do not cancel anything.
- **Replicated data on standby:** The standby's replicated data is retained locally but becomes stale. If HA is re-enabled later, a fresh snapshot bootstrap (MA-3.2) brings the standby back to current state.

---

## 19. New Enhancements

**Reviewer:** Claude (automated architecture review)
**Review date:** 2026-03-15
**Reviewed against:** Requirements.md v1.2, HighLevelRequirements.md v0.3, codebase as of 2026-03-15

This section captures gaps, inconsistencies, and improvement suggestions found during a cross-reference review of this plan against the requirements baseline and the current codebase state.

### 19.1 Critical Gaps

#### GAP-1: CANDIDATE state is referenced but never defined

MA-0.1 instruction #4 asks for a state diagram covering `PRIMARY -> STANDBY_HOT -> CANDIDATE -> RECOVERING`. The verification snapshot also flags this as incomplete. However, section 4.1 (Runtime roles) defines five states — PRIMARY, STANDBY_HOT, RECOVERING, READ_ONLY, OFFLINE — and `CANDIDATE` is not among them.

**Impact:** Without a defined CANDIDATE state, the election window is ambiguous. When a standby begins an election attempt, what state is it in? Can it still serve proxied requests? Can two agents be CANDIDATE simultaneously?

**Suggestion:** Add CANDIDATE to section 4.1 with clear entry/exit conditions:
- Entry: standby detects primary failure and passes pre-election checks
- Behavior: cannot serve authoritative writes, cannot accept FCC commands, broadcasts claim
- Exit: claim succeeds → PRIMARY, higher-priority claim observed → STANDBY_HOT, timeout → STANDBY_HOT
- Invariant: multiple simultaneous CANDIDATEs are possible but at most one transitions to PRIMARY (highest priority wins)

#### GAP-2: Desktop Edge Agent is optional — plan text and HLD should reflect this

The desktop agent is **not required**. A single Android agent running alone is a valid production topology. The plan text in §1 ("one Desktop Edge Agent and one or more Android Edge Agents") and §4.2 (priority policy listing desktop first) reads as though desktop is always present. This should be clarified throughout.

Additionally, Requirements.md REQ-15 specifies only the Android agent (Kotlin/Java, Urovo i9100). The desktop agent (C#/.NET, Avalonia UI, Windows) has no formal requirements document, no WIP-HLD-Desktop-Edge-Agent.md, and DesktopFunctionalFindings.md shows it is feature-complete as standalone but has zero multi-agent runtime code.

**Impact:** The HA model must degrade gracefully across all valid topologies:

| Topology | HA Behavior |
|---|---|
| 1 Android agent only | Trivially PRIMARY. No failover possible. HA monitoring reports "no promotable standby." |
| 2+ Android agents, no desktop | Full HA between Android peers. Highest-priority Android is PRIMARY. |
| 1 desktop + 1+ Android agents | Desktop-preferred PRIMARY. Android standby(s) available for failover. |
| 1 desktop only | Trivially PRIMARY. No failover possible. |

**Suggestion:**
- Update §1, §4.2, and the objective statement to explicitly list all valid topologies including Android-only
- The election algorithm, replication design, and peer discovery must all work for the 2-Android-no-desktop case — not just the desktop+Android case
- Before Phase 2, create `WIP-HLD-Desktop-Edge-Agent.md` covering desktop-specific HA concerns (Windows service lifecycle, Kestrel peer API hosting, LAN firewall rules, DPAPI credential storage, FCC session management during promotion/demotion). This is needed only when a desktop agent is deployed, but the contract must be defined so Android agents can interoperate with it.

#### GAP-3: FCC session handover during failover is unaddressed — RESOLVED

> **Resolution:** See `docs/specs/research/fcc-session-handover.md` (P2-16). All four vendors documented. DOMS is the only persistent-session vendor; Radix/Advatec/Petronite are stateless and failover-transparent.

The plan states "one active primary owns the FCC" and "open FCC ownership only after claim succeeds" (MA-5.2), but does not specify what FCC ownership means at the protocol level.

Most FCC vendors (DOMS, Radix, Advatec, Petronite) maintain a persistent or semi-persistent session with their controlling client. Questions the plan does not answer:

- Does the new primary need to re-establish the FCC connection from scratch?
- What happens to the old primary's FCC session? Does it time out, or must it be explicitly closed?
- Can two agents hold simultaneous FCC sessions? If so, epoch fencing at the cloud doesn't prevent duplicate FCC commands at the device level.
- How long does FCC session establishment take per vendor? This directly affects the 30-second failover target.
- What happens to in-flight FCC commands (e.g., a pre-auth PENDING response) when the primary fails mid-command?

**Impact:** The FCC is the real split-brain risk. Epoch fencing protects cloud writes, but it does not prevent two agents from simultaneously sending commands to the same FCC over LAN.

**Suggestion:** Add a new work item (or extend MA-5.2) covering FCC session ownership transfer:
- Define vendor-specific session takeover behavior (or document it as a research task per vendor)
- Require the new primary to verify the old primary's FCC session is closed before opening a new one (or accept the risk and document it)
- Add FCC session establishment time to the failover budget (30s target minus election time minus FCC reconnect time = available replication catch-up time)
- Add a test scenario: "primary fails mid-pre-auth-command — new primary must detect the orphaned pre-auth and handle it"

#### GAP-4: In-flight pre-auth during failover — RESOLVED

> **Resolution:** See MA-5.4 (§12) for pre-auth replication via heartbeat piggyback, adoption behavior per state, UNKNOWN state handling, and three new §15 test scenarios.

The plan says "no duplicate FCC command execution" (success criteria) and "no buffered transaction or pre-auth record is lost during failover" but does not address pre-auths that are in intermediate states when the primary fails:

| Pre-Auth State | Risk if Primary Fails |
|---|---|
| PENDING (sent to FCC, no response yet) | New primary doesn't know if FCC authorized the pump. Re-sending may double-authorize. Not re-sending may leave the pump hanging. |
| AUTHORIZED (pump authorized, not yet dispensing) | Pump is live. New primary must know about this authorization to track the eventual dispense. |
| DISPENSING (fuel flowing) | Fuel is physically flowing. The dispense-complete event must be captured by someone. |

**Suggestion:** The replication data model (MA-3.1) lists "pre-auth queue and status" as replicated state, but the plan should explicitly address:
- Pre-auth records must be replicated to standby in near-real-time (not just at delta-sync intervals)
- On promotion, the new primary must adopt all non-terminal pre-auths and resume tracking them
- If the FCC returns a dispense-complete for a pre-auth that was started by the old primary, the new primary must be able to match it
- Add a specific test scenario to section 15

#### GAP-5: Ingestion mode interaction with failover — RESOLVED

> **Resolution:** See MA-5.5 (§12) for per-mode failover behavior. CLOUD_DIRECT is transparent; RELAY uses option (c) — accept push interruption, catch-up poll covers gap; BUFFER_ALWAYS quantifies stranded buffer risk.

Requirements.md REQ-12 defines three ingestion modes (CLOUD_DIRECT, RELAY, BUFFER_ALWAYS). The plan does not address how failover interacts with these modes:

- **RELAY mode:** The FCC is configured to push to the Edge Agent. If the primary agent fails, the FCC's push target is now unreachable. The new primary is a different device with a different IP. The FCC cannot be reconfigured automatically (it's physical hardware with manual configuration). This means RELAY mode may be incompatible with automatic failover unless the FCC supports DNS-based or VIP-based targeting.
- **BUFFER_ALWAYS mode:** The primary's local buffer contains un-synced transactions. On failover, those transactions are stranded on the failed device until it recovers. The new primary starts with whatever was replicated.

**Suggestion:** Add a subsection to Phase 5 or Phase 4 addressing ingestion mode constraints:
- CLOUD_DIRECT: failover is transparent (FCC pushes to cloud regardless of which agent is primary)
- RELAY: failover requires either (a) FCC reconfiguration, (b) a shared VIP/floating IP on the LAN, or (c) accepting that FCC push is interrupted until the old primary recovers. Document which approach is chosen.
- BUFFER_ALWAYS: the stranded buffer is a known data-at-risk window. Quantify the risk (max = sync interval × transaction rate) and document whether this is acceptable.

### 19.2 Design Concerns

#### DESIGN-1: Election algorithm is underspecified

MA-5.2 says "priority-based election with epochs" but does not define the algorithm. With 2-3 agents, the edge cases are significant:

- Is there a quorum requirement? With 2 agents, one failure means no quorum. With 3 agents, a LAN partition of 1 vs 2 means the partition of 1 cannot form a quorum either.
- What if two agents have the same priority and both detect primary failure simultaneously?
- How long does an election round take? What's the timeout before a new round starts?
- Does the cloud participate in the election, or is it purely peer-to-peer?

**Suggestion:** Define the election algorithm explicitly. Given the small cluster size (2-3 agents), a simple approach works:
1. On suspected primary failure, wait a randomized back-off proportional to inverse priority (higher priority waits less)
2. Broadcast a claim with `newEpoch = max(seen epochs) + 1`
3. If no higher-priority claim is received within `electionTimeoutSeconds`, assume leadership
4. Register the new epoch with the cloud (or locally persist if cloud is unreachable)
5. If cloud is unreachable, the new leader must register its epoch on next cloud contact

Also clarify: can the cloud serve as a tiebreaker when reachable? The current `SiteHaLeadershipResolver` in the cloud already has deterministic leader selection logic — this could be the authoritative arbiter.

#### DESIGN-2: Cloud's role during elections is ambiguous

The test matrix says "LAN up, internet down → local election still works" — which implies elections are peer-to-peer. But the cloud has `SiteHaLeadershipResolver` with deterministic leader selection. These two mechanisms could conflict:

- Agent A wins local election and becomes PRIMARY with epoch 5
- Cloud still thinks Agent B is leader at epoch 4 (cloud was unreachable during election)
- Agent A connects to cloud and tries to upload with epoch 5
- The cloud's `AuthoritativeWriteFenceService` resolves the leader snapshot — does it accept epoch 5 from Agent A, or reject it because the cloud's own resolver still points to Agent B?

**Suggestion:** Define a clear precedence rule:
- Option A: Cloud is authoritative. Local elections are proposals; they become official only when the cloud confirms. Downside: elections fail during internet outages.
- Option B: Agent-side elections are authoritative. The cloud accepts any epoch higher than what it has seen. The cloud's resolver is read-only (it reflects agent-reported state, not cloud-decided state). Upside: elections work offline.
- Recommend Option B for resilience, since the primary value proposition of multi-agent HA is continued operation during connectivity loss.

#### DESIGN-3: Clock synchronization is unaddressed

The plan uses time-based thresholds extensively (heartbeat intervals, failover timeouts, replication lag in seconds, `leaderSinceUtc`). Android devices and Windows machines at African fuel stations may have significant clock skew, especially during extended internet outages when NTP is unavailable.

**Suggestion:**
- Use monotonic/logical clocks (sequence numbers) for replication ordering instead of wall-clock timestamps
- Use heartbeat round-trip measurement for failure detection instead of absolute timestamps
- Document an acceptable clock skew tolerance (e.g., ±30 seconds) and add a peer clock-skew check to the health endpoint
- Consider adding a clock-skew warning to diagnostics

#### DESIGN-4: Phase 4 ordering may cause rework

The localhost facade (Phase 4) defines how standby Android agents proxy requests to the primary. But Phases 2 and 3 build peer connectivity and replication without knowing the proxy contract. This means:

- Phase 2 peer API design may not account for proxy use cases (e.g., `POST /peer/proxy/preauth` is listed in MA-2.1, but the proxy behavior is only specified in MA-4.1)
- Phase 3 replication design may not account for "serve from local cache when freshness permits" (MA-4.1 instruction #2), because freshness rules aren't defined until Phase 4

**Suggestion:** Move the localhost facade **contract definition** (not implementation) into Phase 2 alongside MA-2.1. The actual proxy implementation can stay in Phase 4, but the interface contract should inform peer API design.

#### DESIGN-5: READ_ONLY role is defined but never used

Section 4.1 defines READ_ONLY as "can serve cached reads, cannot become primary." However:
- No phase assigns or transitions to READ_ONLY
- No test scenario exercises it
- No acceptance criteria mention it
- The cloud's `SiteHaLeadershipResolver` excludes READ_ONLY from leader selection, but nothing in the agent runtime sets this state

**Suggestion:** Either:
- Remove READ_ONLY from the runtime roles and simplify the state model, OR
- Define when an agent enters READ_ONLY (e.g., agent with `roleCapability = READ_ONLY` in config, or an agent that fails integrity checks) and add a test scenario

#### DESIGN-6: Rollback and degradation plan is missing

> **Resolution:** See "Emergency HA Disable and Rollback (P2-22)" section above. Covers: disable procedure (`siteHa.enabled = false`), primary determination after disable, cloud dashboard emergency button, feature flag strategy (`autoFailoverEnabled`), and data safety guarantees.

~~If multi-agent HA causes issues in production (e.g., election storms, replication bugs, split-brain incidents), the plan has no documented fallback.~~

### 19.3 Operational and Testing Gaps

#### OPS-1: FCC vendor-specific takeover testing should start earlier

The risk table identifies "FCC vendor session behavior on takeover" as a major risk, but the mitigation ("vendor-specific takeover tests") is scheduled for Phase 6 (hardening). If vendor session behavior is incompatible with the failover model, discovering it in Phase 6 could force a redesign.

**Suggestion:** Add a research task to Phase 2 (or even Phase 0): for each supported FCC vendor, document:
- Can two clients hold simultaneous sessions?
- What happens to the old session when a new client connects?
- How long does session establishment take?
- Does the FCC queue commands during a session gap?
- Is there a "session takeover" or "force disconnect" API?

This research should gate the peer API design, not follow it.

#### OPS-2: Android background execution constraints need concrete mitigations

The risk table mentions "Android background constraints" with a vague mitigation. The plan requires standby Android agents to:
- Send peer heartbeats every N seconds
- Receive and apply replication deltas
- Monitor primary health
- Be ready to promote within 30 seconds

Android 12 (Urovo i9100) has strict background limits:
- Foreground service notifications are required (already used)
- Doze mode can defer network activity by minutes
- Battery optimization can kill foreground services on some OEMs
- The Urovo i9100 is a rugged device — its Android customization may be more permissive, but this needs verification

**Suggestion:** Add specific mitigations to Phase 2:
- Verify Urovo i9100 OEM battery optimization behavior for foreground services
- Document required Android battery exemption settings for the Edge Agent
- Define a "heartbeat cadence contract": what's the minimum heartbeat interval that Android can reliably sustain in the foreground? Use this to set `heartbeatIntervalSeconds` floor.
- Add a test: "Android agent in standby mode, device idle for 30 minutes, primary fails — does promotion happen within 30 seconds?"

#### OPS-3: No alerting defined for failover events

> **Resolution:** Alerting rules have been added to MA-6.2 (P2-23) with a detailed table of 6 alerts covering failover, no-promotable-standby, replication lag, election flapping, stale-writer rejection, and peer directory staleness. Alerts are deliverable via configurable channels (webhook, email, dashboard).

~~MA-1.3 (operational visibility) shows failover events in a dashboard, but there is no alerting.~~

#### OPS-4: Missing test scenario — simultaneous agent startup

> **Resolution:** Cold-start election behavior is now defined in MA-5.1a (P2-24) covering four scenarios: single agent, two simultaneous agents, cloud-unreachable, and existing leader. Three cold-start test scenarios have been added to §15 test matrix.

~~The test matrix includes "Two agents boot at the same time" with expected result "exactly one wins leadership after election settles." However, this scenario has a subtlety not addressed.~~

### 19.4 Minor Issues

| # | Issue | Location | Suggestion |
|---|---|---|---|
| M-1 | **Resolved — see GAP-2 update.** Android-only (no desktop) is a confirmed valid topology. Plan text in §1, §4.2 should be updated. | §1, §4.2 | Rewrite §1 objective to say "one or more Edge Agents (Android and/or Desktop)". Update §4.2 to list Android-only as the first example topology. |
| M-2 | **Elevated — see section 19.6 below.** Peer directory change propagation (new agent joins, agent leaves, role changes) has no defined notification mechanism. Current config poll latency is 60–300 seconds, which is too slow for HA peer discovery. | MA-1.2, MA-2.1 | See 19.6 for detailed config sync and peer discovery recommendation. |
| M-3 | MA-3.1 lists "nozzle mappings" as replicated state, but nozzle mappings are master data synced from Odoo via Databricks (REQ-3). Why replicate them peer-to-peer? | MA-3.1 | Clarify: is this for offline resilience (standby has mappings even if it hasn't synced with cloud), or is it an error? If it's for offline resilience, say so explicitly. |
| M-4 | Sprint estimates total 12 sprints (24 weeks / ~6 months). Given that Phases 2-5 contain the hardest distributed systems work and nothing is implemented yet, this may be optimistic. | §6 | Consider adding a buffer sprint between Phase 5 and Phase 6 for integration stabilization. |
| M-5 | The cross-phase dependency map (§14) doesn't show MA-0.3 (test strategy) as a dependency for anything, but test infrastructure should gate Phase 2+ acceptance. | §14 | Add MA-0.3 as a dependency for MA-2.2 and MA-2.3 (or at least for Phase 6). |
| M-6 | `OFFLINE` runtime role (§4.1) vs `Fully Offline` connectivity state (HLR §15.9) — are these the same concept? An agent could be in OFFLINE role but the device is powered on. | §4.1 | Clarify the relationship. Suggest: OFFLINE role means "not participating in HA" (e.g., manually disabled or version-incompatible), while Fully Offline connectivity means "no network." |

### 19.5 What the Plan Gets Right

For balance, the following design decisions and structural choices are well-considered:

1. **Active-standby over active-active** — correct for FCC control, which is inherently single-owner.
2. **Epoch-based fencing** — the right primitive for distributed leader validity. Cloud-side enforcement is already implemented and tested.
3. **No auto-failback** — prevents leader flapping and simplifies recovery reasoning.
4. **Warm replication before automatic promotion** — guards against promoting an empty standby.
5. **Phased delivery with clear dependencies** — the dependency map (§14) is well-structured.
6. **Desktop-preferred but configurable** — avoids hardcoding a topology assumption.
7. **Localhost contract preservation** — critical for Odoo POS compatibility and correctly identified as a first-class constraint.
8. **The test matrix (§15)** covers the right failure scenarios.
9. **Cloud-side write fencing is already live** — gives immediate protection even before full failover is implemented.

### 19.6 Recommended Config Sync and Peer Discovery Design

This section addresses M-2 (peer directory change propagation) and the broader question of how agent configuration — especially the peer directory — should be kept current across all agents at a site.

#### 19.6.1 Problem statement

The current config delivery mechanism is poll-based:
- Agents call `GET /api/v1/agent/config` with `If-None-Match: <configVersion>`.
- Cloud returns 304 if unchanged, or the full config snapshot if the version has advanced.
- Default poll interval: **300 seconds (Android), ~60 seconds (Desktop)**.
- The peer directory (which agents exist, their endpoints, roles, epochs) is built dynamically by `SiteHaLeadershipResolver.ResolveSnapshot()` on every config request.

When a new agent registers at a site, or an existing agent changes status:
- The cloud updates its database immediately.
- **No notification is sent to existing agents.** They discover the change on their next config poll.
- Worst-case discovery latency: **5 minutes** (Android default config poll).

For HA, this is too slow. A new standby cannot begin receiving replication or heartbeats until the primary knows it exists. An agent that goes offline stays in the peer directory until someone polls and sees the updated status.

#### 19.6.2 Config categories and freshness requirements

Not all configuration has the same freshness requirement:

| Category | Examples | Change Frequency | Required Freshness |
|---|---|---|---|
| **Site/FCC config** | Vendor, IP, port, pump mappings, fiscalization | Rare (days/weeks) | Minutes — next config poll is fine |
| **HA parameters** | Heartbeat interval, failover timeout, priority | Rare (admin action) | Minutes — next config poll is fine |
| **Peer directory** | Agent list, endpoints, roles, epochs, heartbeat timestamps | Moderate (agent joins/leaves/fails) | **Seconds** — must propagate within one heartbeat cycle |
| **Runtime leader state** | Current leader, current epoch | Dynamic (on failover) | **Sub-second** — handled by peer heartbeats, not config polling |

The key insight: **peer directory changes need faster propagation than general config changes**, but runtime leader state should not depend on cloud config polling at all (it flows via peer heartbeats on the LAN).

#### 19.6.3 Recommended multi-layer design

Use three layers that progressively reduce discovery latency, each building on existing infrastructure:

**Layer 1: Piggyback `peerDirectoryVersion` on existing cloud traffic (primary mechanism)**

The cloud maintains a monotonically increasing `peerDirectoryVersion` integer per site. It increments when:
- A new agent registers at the site
- An agent is deactivated or decommissioned
- An agent's role capability, priority, or peer API metadata changes
- The cloud detects a stale heartbeat and marks an agent OFFLINE

Every cloud API response to an agent includes a `X-Peer-Directory-Version` response header with the current site version. Agents already contact the cloud frequently:
- Health/telemetry upload: every ~30 seconds (desktop cadence tick)
- Transaction upload: on-demand
- Config poll: every 60–300 seconds
- Command poll: every cadence tick

On every cloud response, the agent compares the received `peerDirectoryVersion` with its locally cached version. If stale, the agent triggers an **immediate out-of-band config fetch** (not waiting for the next scheduled config poll).

**Worst-case latency:** ~30 seconds (next cadence tick that contacts the cloud).
**Cost:** One integer comparison per cloud response. Near zero.

**Layer 2: Cloud agent command `REFRESH_CONFIG` (accelerator)**

When a peer-directory-affecting event occurs, the cloud enqueues a `REFRESH_CONFIG` command for every other active agent at the same site. Agents already poll for commands on their cadence tick. Receiving this command triggers an immediate config fetch.

This is additive to Layer 1 — it ensures the agent fetches config even if its next cloud contact would have been a 304 config poll (which wouldn't include the `peerDirectoryVersion` header). The command queue is the existing mechanism; no new infrastructure is needed.

**Worst-case latency:** Same as Layer 1 (~30 seconds), but more reliable because it doesn't depend on which API the agent contacts first.

**Layer 3: LAN peer announcement (supplement for instant discovery)**

When an agent starts up or re-registers, it broadcasts a UDP announcement on the station LAN containing:
- `agentId`
- `siteCode`
- `peerApiBaseUrl` and `peerApiPort`
- `peerDirectoryVersion` (as known to this agent)

Existing agents listening on the announcement port can:
1. Add the new peer to their local peer cache immediately (before cloud confirmation)
2. Trigger an immediate cloud config fetch to get the authoritative peer directory
3. Begin sending heartbeats to the new peer

This is supplementary — LAN broadcast is unreliable (may be blocked by network configuration, switch ACLs, or VLAN isolation). Agents must never depend solely on LAN announcements for peer discovery. Layer 1 + Layer 2 are the reliable mechanisms.

**Best-case latency:** Near instant.
**Availability:** Only when both agents are on the same LAN segment.

#### 19.6.4 Event flow: new agent joins a site

```
1. New agent calls POST /api/v1/agent/register
2. Cloud:
   a. Creates agent record in site-agent registry
   b. Increments site peerDirectoryVersion
   c. Enqueues REFRESH_CONFIG command for all other active agents at the site
   d. Returns registration response (with initial config including peer directory)
3. New agent:
   a. Persists config locally
   b. Broadcasts LAN announcement (Layer 3)
   c. Begins heartbeat cycle to known peers from peer directory
4. Existing agents (whichever happens first):
   a. Receive LAN announcement → add peer to local cache, trigger config fetch
   b. Contact cloud for any reason → see stale peerDirectoryVersion in response header → trigger config fetch
   c. Poll commands → receive REFRESH_CONFIG → trigger config fetch
5. Within ≤30 seconds (worst case), all agents have updated peer directory
6. Peer heartbeats begin between all agents
```

#### 19.6.5 Event flow: agent goes offline / fails

```
1. Peer heartbeats from agent stop arriving
2. Remaining agents mark the peer as suspected (local detection — no cloud involvement)
3. If suspected agent was PRIMARY → election logic triggers (Phase 5)
4. On next cloud contact, surviving agents report updated heartbeat/status
5. Cloud detects stale heartbeat for the failed agent → marks it OFFLINE
6. Cloud increments peerDirectoryVersion
7. Cloud enqueues REFRESH_CONFIG for surviving agents
8. Surviving agents fetch updated peer directory (confirms cloud-side view matches local detection)
```

Note: peer failure detection is primarily a **LAN-side responsibility** (peer heartbeats). The cloud's role is to confirm and record the state change, not to be the first to detect it. This is important because the cloud may be unreachable when a peer fails.

#### 19.6.6 Config poll interval recommendation for HA-enabled sites

For sites with `siteHa.enabled = true`, the config poll interval should be reduced:

| Setting | Current Default | Recommended for HA |
|---|---|---|
| `configPollIntervalSeconds` (Android) | 300s | **60s** |
| `configPollIntervalSeconds` (Desktop) | 60s | **60s** (no change needed) |
| Cloud config cache TTL | 60s | **30s** (to surface peer directory changes faster) |

The reduced interval is a safety net only — Layers 1-3 above handle rapid peer discovery. The interval matters only if all three layers fail (e.g., cloud unreachable and LAN announcement missed). At 60 seconds, the agent still refreshes config on a reasonable cadence even in degraded conditions.

#### 19.6.7 What changes in existing code

| Component | Change Required | Effort |
|---|---|---|
| **Cloud — RegisterDeviceHandler** | After successful registration, increment `peerDirectoryVersion` for the site and enqueue `REFRESH_CONFIG` command for other agents | Small |
| **Cloud — GetAgentConfigHandler** | Include `X-Peer-Directory-Version` header in all config responses | Small |
| **Cloud — All agent-facing API responses** | Include `X-Peer-Directory-Version` header (or add it as middleware) | Small |
| **Cloud — Site model** | Add `peerDirectoryVersion` column (integer, default 0) | Small |
| **Android — CadenceController / ConfigPollWorker** | Check `X-Peer-Directory-Version` header on every cloud response; trigger config re-fetch if stale | Small |
| **Android — Agent command handler** | Handle `REFRESH_CONFIG` command (trigger immediate config poll) | Small |
| **Desktop — CadenceController / ConfigPollWorker** | Same as Android: check header, trigger re-fetch if stale | Small |
| **Desktop — Agent command handler** | Handle `REFRESH_CONFIG` command | Small |
| **Both agents — LAN announcer** | UDP broadcast on startup/registration with agent metadata | Medium (new component, but simple) |
| **Both agents — LAN listener** | UDP listener that triggers config refresh on peer announcement | Medium (new component) |

All changes build on existing infrastructure. No new polling loops, no WebSocket server, no push notification dependency.

#### 19.6.8 What this does NOT solve

This design addresses **peer directory propagation** — how agents learn about each other's existence and endpoints. It does **not** replace:

- **Peer heartbeats** (Phase 2): once agents know about each other, they exchange health directly over LAN. Heartbeats are peer-to-peer, not cloud-mediated.
- **Replication** (Phase 3): transaction and pre-auth state sync is peer-to-peer over LAN.
- **Election** (Phase 5): leader election is peer-to-peer (with optional cloud confirmation).
- **Runtime leader state**: the current leader and epoch are communicated via peer heartbeats, not via config polling.

The cloud's role in HA is: (1) authoritative peer directory, (2) config delivery, (3) write fencing, (4) operational visibility. The cloud is **not** in the real-time HA control path — that is entirely peer-to-peer on the LAN.

#### 19.6.9 Relationship to plan phases

This config sync design should be implemented as part of **Phase 1 (MA-1.2)** since it extends the registration flow and config delivery. The LAN announcement component (Layer 3) can be deferred to **Phase 2 (MA-2.2 / MA-2.3)** alongside the peer API work, since it shares the same LAN networking infrastructure.

The dependency order is:
1. `peerDirectoryVersion` column and cloud header (Phase 1)
2. `REFRESH_CONFIG` command enqueue on registration (Phase 1)
3. Agent-side header checking and command handling (Phase 1)
4. LAN announcement broadcast and listener (Phase 2)
