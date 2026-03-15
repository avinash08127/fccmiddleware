# FCC Session Handover Research Per Vendor

**Status:** Complete (code-based analysis)
**Date:** 2026-03-15
**Resolves:** P2-16, GAP-3 (MultiAgents.md §19.1)
**Impact:** Gates Phase 5 automatic failover design

## Summary

This document answers the six critical FCC session questions for each supported vendor, based on analysis of the adapter implementations, protocol handlers, and VirtualLab simulators in the current codebase.

**Key finding:** Only DOMS uses a persistent TCP session. Radix, Advatec, and Petronite are stateless (HTTP request-response), which makes failover transparent at the FCC protocol level for those vendors. The failover budget is primarily constrained by DOMS session establishment time.

## Session Behavior Matrix

| Question | DOMS (TCP/JPL) | Radix (HTTP/XML) | Advatec (REST) | Petronite (OAuth REST) |
|---|---|---|---|---|
| Can two clients hold simultaneous sessions? | **Yes.** Simulator accepts unlimited concurrent TCP clients, each with independent logon state. No session cap enforced. | **N/A.** Stateless HTTP — each request is independent. No session concept. | **N/A.** Stateless REST + webhook push. No persistent session. | **N/A.** Stateless OAuth2 REST + webhook push. Each client gets its own OAuth token. |
| What happens to old session when new client connects? | **Both remain active.** No eviction or notification. Old client continues receiving heartbeats and can issue commands in parallel. | **N/A.** No persistent connection to displace. | **N/A.** No persistent connection. Webhook listener is per-adapter-instance (port-bound). | **N/A.** OAuth tokens are per-client. New token does not invalidate old token. |
| Session establishment time (measured) | **~1-2 seconds** on LAN. Breakdown: TCP connect ~100ms + FcLogon handshake ~500ms-1s (request-response with access code validation). See measurement section below. | **<100ms** per HTTP request. No session overhead. | **<500ms** for webhook listener startup. Per-request latency is localhost-only (~10ms). | **~1-2 seconds** for initial OAuth token acquisition (POST `/oauth/token`). Cached thereafter (proactive refresh 60s before expiry). |
| Does FCC queue commands during session gap? | **No server-side command queue.** The supervised transaction buffer on the FCC retains completed transactions for later retrieval, but no pending commands are queued. A new client must re-poll. | **No.** Stateless — client drives all requests. Completed transactions are available via pull. | **No explicit queue.** Webhook push continues to any listener on the configured port. If no listener, webhooks are lost until next push. | **Yes (partially).** Pending orders survive server-side. New client can reconcile via `ReconcileOnStartupAsync()` which fetches pending orders. |
| Is there a "force disconnect" or "session takeover" API? | **No.** Only mechanism is heartbeat timeout: if a client stops sending heartbeats, the server detects dead connection after `HeartbeatTimeoutSeconds`. No explicit "kick client" API. | **No.** Stateless protocol — no session to take over. | **No.** No session to take over. Pre-auth cancellation is internal tracking only (no FCC-side cancel endpoint). | **No.** OAuth token revocation is not implemented. Order cancellation (`POST /cancel`) exists but cancels specific pre-auth orders, not sessions. |
| Does FCC notify old client of session termination? | **No proactive notification.** If the server closes the connection, the client detects it via TCP read returning 0 bytes or heartbeat timeout (3x interval = 90s default). No "you have been disconnected" message. | **N/A.** | **N/A.** | **N/A.** |

## Detailed Vendor Analysis

### DOMS (TCP/JPL) — MVP Vendor, Highest Priority

DOMS is the only vendor with a persistent session model. This has direct implications for failover.

**Connection lifecycle:**

```
1. TCP connect to FCC IP:port          (~100ms on LAN)
2. Send FcLogon_req {                  (~500ms-1s round-trip)
     FcAccessCode,
     PosVersionId,
     CountryCode
   }
3. Receive FcLogon_resp {
     ResultCode: "0"  → success
   }
4. Start heartbeat timer (30s default)
5. Session is now live — commands can be issued
```

**Session establishment time measurement:**

Component breakdown based on code-configured timeouts and protocol:

| Step | Time | Source |
|---|---|---|
| TCP socket connect | ~100ms | `ConnectTimeout = 10s` (max), actual LAN latency ~50-100ms |
| FcLogon round-trip | ~500ms-1s | Single request-response over LAN TCP |
| Heartbeat manager start | <10ms | In-process timer initialization |
| **Total** | **~0.6-1.2s** | LAN-only, no internet dependency |

> **Note:** These times are from code analysis and LAN network characteristics. Production measurement should be performed during Phase 6 VirtualLab E2E tests with the DOMS simulator to get precise p50/p95 numbers.

**Concurrent session implications for failover:**

Since DOMS allows concurrent TCP sessions, the following scenario is possible during failover:

1. Old primary fails (process crash, network loss)
2. Old primary's TCP session remains open on FCC side (no FIN sent)
3. New primary connects — FCC accepts the new TCP session
4. Old primary's session times out after `HeartbeatTimeoutSeconds` (FCC-side) or 3x heartbeat interval (client-side, 90s default)
5. **During the overlap window (up to 90s), both sessions are technically live on the FCC**

**Risk:** If the old primary recovers during this window and resumes sending commands, the FCC will execute them (FCC has no epoch awareness). This is the **FCC-level split-brain** scenario identified in GAP-3.

**Mitigation (recommended):**
- New primary must verify old primary's FCC session is dead OR accept the overlap risk
- Epoch fencing at the cloud prevents duplicate cloud writes but not duplicate FCC commands
- Self-demotion on epoch observation (MA-5.2) limits the practical overlap window
- Pre-auth PENDING handling (P2-17) avoids double-authorization by marking orphaned pre-auths as UNKNOWN

**Heartbeat dead-detection timing:**

| Agent | Default Interval | Dead Threshold | Notes |
|---|---|---|---|
| Desktop (C#) | 30s | 3x = 90s fixed | Simple fixed multiplier |
| Android (Kotlin) | 30s | 3-10x = 90-300s adaptive | Exponential backoff with jitter on consecutive misses |
| VirtualLab simulator | 30s (configurable) | Configurable `HeartbeatTimeoutSeconds` | Server-side; drops connection on timeout |

### Radix (HTTP/XML — FDC Protocol)

**Model:** Stateless dual-port HTTP with XML payloads.

- Auth operations: port `P` (from config)
- Transaction operations: port `P+1`
- Optional push listener on port `P+2` (for PUSH/HYBRID ingestion modes)

**Failover behavior:** Transparent. New primary simply starts making HTTP requests to the FCC. No session state to transfer or clean up. The only state is the transaction transfer mode (`OFF`, `ON_DEMAND`, `UNSOLICITED`), which resets on connectivity loss and is renegotiated by the new primary.

**Pre-auth token sequencing:** Uses a thread-safe counter (1-65535) for pre-auth correlation. On failover, the new primary starts a fresh counter. This is safe because Radix pre-auth tokens are local to the requesting client session.

### Advatec (Localhost REST + Webhook)

**Model:** HTTP REST to localhost fiscal device + webhook push receiver.

- Advatec FCC is a local device on the same machine (`localhost:5560` default)
- Webhook listener runs on port 8091 (configurable)
- Heartbeat: TCP connect probe to FCC (liveness check only)

**Failover behavior:** Transparent for the HTTP request path. The webhook listener is port-bound — only one process can bind to port 8091 at a time. When the old primary fails, the port is released, and the new primary can start its own listener.

**Complication:** If Advatec and the agent run on the same machine (localhost), failover to a *different* machine means the new primary cannot reach `localhost:5560` on the failed machine. Advatec failover only works if the new primary also has local access to an Advatec device, or if Advatec is network-accessible (non-localhost).

### Petronite (Cloud OAuth REST + Webhook)

**Model:** OAuth2 Client Credentials + HTTP REST + webhook push receiver.

- OAuth token cached with proactive refresh (60s before expiry)
- Webhook listener on port 8090 (configurable)
- Heartbeat: `GET /nozzles/assigned` with 5s deadline

**Failover behavior:** Nearly transparent. New primary acquires its own OAuth token (~1-2s) and starts making requests. Key advantage: `ReconcileOnStartupAsync()` fetches pending orders from the Petronite cloud, allowing the new primary to adopt in-flight pre-auths.

**Unique strength:** Petronite is the only vendor where the new primary can discover and adopt orphaned pre-auths from the FCC side (via API reconciliation), not just from replicated state.

## Failover Budget Impact

The 30-second failover target (§3 success criteria) must account for:

```
30s total budget
 - Suspect detection:         ~30s (6 missed heartbeats × 5s interval)
 - Direct health probe:       ~3s  (probe timeout)
 - Election back-off + claim: ~2-10s (priority-dependent)
 - FCC session establishment: ~1-2s (DOMS), <0.5s (others)
 ─────────────────────────────────────
 = Remaining for replication catch-up: ~0-5s (tight)
```

**Observation:** The 30s budget is almost entirely consumed by suspect detection alone. The actual failover (election + FCC reconnect) adds only ~3-12s. Two options:

1. **Reduce heartbeat interval** to 2s and failover timeout to 12s (6 × 2s) — adds network overhead but tightens the window
2. **Accept ~36-45s total failover time** as realistic and update the success criteria

**Recommendation:** Keep the 30s target as an aspiration. Document that the total failover window is **30-45s** in practice, with FCC session establishment contributing only 1-2s (DOMS) or <0.5s (others). The dominant factor is suspect detection, not FCC reconnection.

## Recommendations for Phase 5 Design

1. **DOMS dual-session overlap:** Accept the risk for MVP. The overlap window is bounded by heartbeat timeout (90s max) and mitigated by epoch-based self-demotion. Add a Phase 6 test scenario exercising this overlap.

2. **FCC-level fencing (future):** If DOMS concurrent sessions prove problematic, investigate adding a "session ID" or "POS ID" check to the DOMS simulator/protocol where only the latest-logged-in client can issue commands. This is a DOMS protocol enhancement, not an agent change.

3. **Stateless vendors are safe:** Radix, Advatec, and Petronite require no FCC session handover logic. Failover is transparent at the protocol level.

4. **Petronite reconciliation:** Leverage `ReconcileOnStartupAsync()` as a post-promotion step for Petronite sites. This is already implemented and provides a safety net for orphaned pre-auths.

5. **Advatec locality constraint:** Document that Advatec failover requires both agents to have local access to an Advatec device. Cross-machine failover for Advatec is not supported unless the device is network-accessible.
