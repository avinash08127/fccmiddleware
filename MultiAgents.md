# Multi-Agent Site - Phasewise Implementation Plan

**Status:** Proposed
**Date:** 2026-03-14
**Scope:** Post-MVP implementation plan for desktop and Android Edge Agents running in parallel with a single active primary and automatic failover
**Sprint Cadence:** 2-week sprints

## 1. Objective

Implement a site-level high-availability model where:

- one Desktop Edge Agent and one or more Android Edge Agents can run in parallel
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
- `RECOVERING`
  - catching up after restart or rejoin
  - not eligible for promotion until replication lag is within threshold
- `READ_ONLY`
  - can serve cached reads
  - cannot become primary
- `OFFLINE`
  - not participating in elections or replication

### 4.2 Primary selection policy

Default site priority order:

1. Desktop agent
2. Highest-priority Android agent
3. Next Android agents by configured priority

This remains configuration-driven because some sites may require Android-first leadership.

## 5. Cross-Application Workstreams

| Workstream | Primary Components | Responsibility |
|---|---|---|
| Control Plane | Cloud Backend, config contracts, registration | Multi-agent identity, leader epoch, peer directory, config |
| Peer Runtime | Android agent, desktop agent | Discovery, heartbeat, election, replication, proxying |
| Local Integration | Android agent, Odoo integration surface | Preserve localhost APIs while enabling remote primary ownership |
| Operations | Cloud dashboard, diagnostics, runbooks | Visibility, manual switchover, troubleshooting, rollout |

## 6. Phase Overview

| Phase | Name | Main Outcome |
|---|---|---|
| 0 | Architecture and Contracts | Multi-agent behavior is specified and testable |
| 1 | Site Agent Identity and Registry | Multiple agents per site are first-class in cloud and config |
| 2 | Peer Connectivity and Discovery | Agents can find each other, authenticate, and exchange health |
| 3 | Replication and Standby Readiness | Standbys hold a warm, promotable copy of site state |
| 4 | Localhost Facade and Planned Switchover | Android HHTs hide the real primary and support operator-driven switchover |
| 5 | Automatic Failover and Recovery | Promotion, fencing, and rejoin behavior are automated |
| 6 | Hardening and Rollout | Failover is proven under fault conditions and ready for pilots |

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
   - sequence number
   - last modified UTC
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
**Prereqs:** MA-2.3, MA-3.2
**Estimated effort:** 1 sprint

**Detailed instructions:**

1. Keep all Odoo-facing APIs on localhost unchanged.
2. When the local Android agent is standby:
   - proxy `POST /api/preauth` to primary
   - proxy live `GET /api/pump-status` to primary
   - serve `GET /api/transactions` from local replicated cache when freshness permits, else proxy
3. Add request correlation IDs so proxied calls are traceable end to end.
4. Add fallback behavior when the local agent cannot reach the current primary:
   - short retry window
   - explicit error classification
   - failover-aware retry path

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

### MA-5.2: Election and epoch fencing

**Components:** Android agent, desktop agent, cloud backend
**Prereqs:** MA-5.1, MA-3.3
**Estimated effort:** 1 sprint

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
2. Add alerting for:
   - no promotable standby
   - replication lag beyond threshold
   - repeated elections
   - stale-writer rejection spikes
3. Add operator-visible role and failover history screens.

**Acceptance criteria:**

- site support staff can identify the current leader and reason about failover state
- operational response steps exist for all critical failure modes

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
| Two agents boot at the same time | Exactly one wins leadership after election settles |
| Standby behind on replication | Automatic promotion blocked or marked emergency with forced catch-up |
| Planned supervisor switchover | Leadership moves with no duplicate FCC commands |
| Former primary returns stale | Node self-demotes and re-enters `RECOVERING` |

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
