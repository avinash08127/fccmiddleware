# Unified FCC Adapter Agent Prompt — DOMS + Radix + Petronite

## Overview

This document covers the architecture, protocol specifics, and implementation standards for all three FCC vendor adapters. Use it as a reference when implementing any adapter task (DOMS-*, RX-*, PN-*, UNI-*).

---

## 1. Protocol Comparison

| Aspect | DOMS (TCP/JPL) | Radix (HTTP/XML) | Petronite (REST/JSON) |
|--------|----------------|-------------------|----------------------|
| **Transport** | TCP persistent, binary STX/ETX framing | HTTP stateless, dual-port | REST stateless, OAuth2 |
| **Auth** | FcLogon handshake (access code) | SHA-1 message signing + USN-Code header | OAuth2 Client Credentials |
| **Transaction Fetch** | Lock-read-clear supervised buffer | FIFO drain: request → ACK → next | Push-only via webhook (no pull) |
| **Pre-auth** | `authorize_Fp_req` JPL message | `<AUTH_DATA>` XML to port P | Two-step: Create Order + Authorize Pump |
| **Volume Format** | Centilitres (integer) | Litres as decimal string | Litres as decimal |
| **Amount Format** | x10 factor | Currency decimal string | Major currency units |
| **Heartbeat** | JPL empty frame `[0x02, 0x03]` at 30s interval | CMD_CODE=55 product read on port P+1 | `GET /nozzles/assigned` liveness |
| **Connection Model** | Persistent TCP socket (IFccConnectionLifecycle) | Stateless HTTP per-request | Stateless HTTP per-request |

---

## 2. Project Structure

### Kotlin Edge Agent
```
src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/
├── common/
│   ├── IFccAdapter.kt                  # Core adapter interface
│   ├── IFccConnectionLifecycle.kt      # Persistent connection lifecycle (DOMS only)
│   ├── IFccEventListener.kt           # Push event callbacks (DOMS only)
│   ├── AdapterTypes.kt                # Shared types + AgentFccConfig
│   ├── Enums.kt                       # FccVendor, PumpState, etc.
│   ├── FccAdapterFactory.kt           # Vendor→adapter resolution
│   └── IFccAdapterFactory.kt          # Factory interface
├── doms/
│   ├── DomsAdapter.kt                 # IFccAdapter + IFccConnectionLifecycle
│   ├── jpl/
│   │   ├── JplFrameCodec.kt          # STX/ETX binary framing
│   │   ├── JplMessage.kt             # Message model
│   │   ├── JplTcpClient.kt           # TCP socket + correlation
│   │   └── JplHeartbeatManager.kt    # Periodic heartbeat
│   ├── protocol/
│   │   ├── DomsLogonHandler.kt        # FcLogon handshake
│   │   ├── DomsPumpStatusParser.kt    # FpStatus parsing
│   │   ├── DomsTransactionParser.kt   # Lock-read-clear
│   │   └── DomsPreAuthHandler.kt      # authorize_Fp
│   ├── mapping/
│   │   └── DomsCanonicalMapper.kt     # Centilitres→microlitres, x10→minor
│   └── model/
│       └── DomsFpMainState.kt         # 14 pump states → canonical
├── radix/
│   ├── RadixAdapter.kt                # IFccAdapter (stateless HTTP)
│   ├── RadixSignatureHelper.kt        # SHA-1 signing
│   ├── RadixXmlBuilder.kt             # XML request construction
│   ├── RadixXmlParser.kt              # XML response parsing
│   └── RadixProtocolDtos.kt           # DTOs
└── petronite/
    ├── PetroniteAdapter.kt            # IFccAdapter (stateless REST)
    ├── PetroniteOAuthClient.kt        # OAuth2 token management
    ├── PetroniteProtocolDtos.kt       # JSON DTOs
    └── PetroniteNozzleResolver.kt     # Nozzle ID bidirectional map
```

### .NET Desktop Agent
```
src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/
├── Common/
│   ├── IFccAdapter.cs
│   ├── IFccConnectionLifecycle.cs
│   ├── IFccEventListener.cs
│   ├── AdapterTypes.cs
│   ├── Enums.cs
│   └── CanonicalTransaction.cs
├── FccAdapterFactory.cs
├── Doms/
│   ├── DomsAdapter.cs                 # REST adapter (existing)
│   ├── DomsJplAdapter.cs              # TCP/JPL adapter (new)
│   ├── DomsProtocolDtos.cs
│   ├── Jpl/
│   │   ├── JplFrameCodec.cs
│   │   ├── JplMessage.cs
│   │   ├── JplTcpClient.cs
│   │   └── JplHeartbeatManager.cs
│   ├── Protocol/
│   │   ├── DomsLogonHandler.cs
│   │   ├── DomsPumpStatusParser.cs
│   │   ├── DomsTransactionParser.cs
│   │   └── DomsPreAuthHandler.cs
│   └── Mapping/
│       └── DomsCanonicalMapper.cs
├── Radix/
│   ├── RadixAdapter.cs
│   ├── RadixSignatureHelper.cs
│   ├── RadixXmlBuilder.cs
│   ├── RadixXmlParser.cs
│   └── RadixProtocolDtos.cs
└── Petronite/
    ├── PetroniteAdapter.cs
    ├── PetroniteOAuthClient.cs
    ├── PetroniteProtocolDtos.cs
    └── PetroniteNozzleResolver.cs
```

### Cloud Backend
```
src/cloud/
├── FccMiddleware.Adapter.Doms/        # Existing
├── FccMiddleware.Adapter.Radix/       # New (Phase 3)
├── FccMiddleware.Adapter.Petronite/   # New (Phase 3)
├── FccMiddleware.Domain/
│   ├── Models/Adapter/SiteFccConfig.cs
│   └── Enums/FccVendor.cs
└── FccMiddleware.Infrastructure/
    └── Adapters/FccAdapterFactory.cs
```

---

## 3. Architecture Rules

### Conversion Rules (ALL adapters)
- **Volume**: Convert to microlitres (Long). DOMS: centilitres × 10,000. Radix: litres × 1,000,000 via BigDecimal. Petronite: litres × 1,000,000.
- **Amount**: Convert to minor currency units (Long). DOMS: value × 10. Radix: parse decimal, multiply by currency decimals. Petronite: major units × 100 (configurable).
- **Timestamps**: Always convert to UTC. Use site timezone from config for local-time sources.
- **No floating-point** for money or volume. Use Long/BigDecimal only.

### Error Handling
- Adapter methods must NEVER throw exceptions. Return failure results instead.
- Network errors → `isRecoverable = true` (caller may retry)
- Auth/parse errors → `isRecoverable = false` (operator intervention needed)

### Dedup Keys
- DOMS: `{siteCode}-{transactionId}`
- Radix: `{FDC_NUM}-{FDC_SAVE_NUM}`
- Petronite: `{siteCode}-{petronite_order_id}`

### Sensitive Fields
- NEVER log: `ApiKey`, `FcAccessCode`, `ClientId`, `ClientSecret`, `WebhookSecret`, `SharedSecret`, `customerTaxId`
- Use `@Sensitive` (Kotlin) or `[SensitiveData]` (.NET) annotations

---

## 4. Testing Standards

### Unit Tests
- Every adapter MUST have unit tests for: normalization, pre-auth, heartbeat, fetch
- Use shared test vectors across Kotlin and .NET for cross-platform consistency
- Golden XML/JSON fixtures in `src/test/resources/` (Kotlin) or test project (NET)

### Integration Tests
- Test against VirtualLab simulators, not real hardware
- Cover: connect/disconnect, fetch + normalize loop, pre-auth lifecycle, error injection

### Cross-Platform Consistency
- Same raw input (XML/JSON) must produce identical CanonicalTransaction output on both platforms
- Shared test vector files ensure this

---

## 5. Vendor-Specific Notes

### DOMS TCP/JPL
- Binary framing: STX (0x02) + JSON payload + ETX (0x03)
- Heartbeat: empty frame `[0x02, 0x03]` — response expected within 3× interval
- FcLogon must complete before any other operation
- Supervised buffer: lock → read → clear (three separate messages)
- 14 pump states map to 9 canonical states

### Radix HTTP/XML
- Dual ports: P (auth), P+1 (transactions)
- Every request signed: SHA-1(XML body + shared secret), wrapped in SIGNATURE element
- FIFO drain: request CMD_CODE=10 → ACK CMD_CODE=201 → loop until RESP_CODE=205
- Token counter: 0–65535, wraps around
- Mode management: ON_DEMAND (polling), UNSOLICITED (push)

### Petronite REST/JSON
- OAuth2 Client Credentials: POST /oauth/token with Basic auth header
- Push-only: transactions arrive via webhook, fetchTransactions returns empty
- Two-step pre-auth: create order → authorize pump (nozzle must be lifted)
- Webhook validation: X-Webhook-Secret header matching
- Nozzle ID resolution: bidirectional map from canonical pump/nozzle ↔ Petronite nozzle ID
- Startup reconciliation: GET pending orders, cancel stale (>30 min), re-adopt recent
