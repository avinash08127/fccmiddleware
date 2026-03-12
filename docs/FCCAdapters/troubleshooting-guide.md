# FCC Adapter Troubleshooting Guide

Common issues, diagnostic steps, and resolutions for each FCC vendor adapter.

---

## General Diagnostics

### Check Adapter Status

**Desktop Agent:** `GET http://localhost:8585/api/status` returns connectivity state, last poll result, and adapter metadata.

**Cloud Backend:** Check telemetry endpoint and site health dashboard for heartbeat status.

### Log Locations

- **Desktop Agent (Windows):** `%LOCALAPPDATA%\FccDesktopAgent\logs\`
- **Edge Agent (Android):** `logcat -s FccMiddleware`
- **Cloud Backend:** Structured logs via Serilog (console + Seq/ELK)

### Connectivity States

| State | Meaning | Action |
|-------|---------|--------|
| `FullyOnline` | Both FCC and internet reachable | Normal operation |
| `InternetDown` | FCC reachable, internet unreachable | Buffering locally; cloud upload suspended |
| `FccUnreachable` | Internet up, FCC heartbeat failing | Check FCC device, LAN, firewall |
| `FullyOffline` | Both unreachable | Local API continues; all sync suspended |

---

## DOMS TCP/JPL Issues

### Connection refused on TCP port

**Symptom:** `Petronite webhook listener failed to start` or TCP connection timeout.

**Diagnosis:**
1. Verify FCC device IP and port: `telnet <IP> <port>`
2. Check Windows firewall allows outbound TCP on the JPL port
3. Verify `connectionProtocol: "TCP"` in site config

**Resolution:** Confirm the DOMS device is powered on and the JPL service is running. Default port is 4001.

### FcLogon rejected

**Symptom:** Log shows `FcLogon_resp error` or `Access code rejected`.

**Diagnosis:**
1. Check `fcAccessCode` in site config matches the DOMS device configuration
2. Verify `domsCountryCode` and `posVersionId` match expected values

**Resolution:** Update access code in Portal site config. Coordinate with DOMS technician.

### Heartbeat timeout / Connection dropped

**Symptom:** `onConnectionLost` event, adapter marks FCC as unreachable.

**Diagnosis:**
1. Check `heartbeatIntervalSeconds` — default 30s, timeout at 3x (90s)
2. Network path stability: packet loss on LAN?
3. DOMS device may have restarted

**Resolution:** Adapter auto-reconnects with exponential backoff (1s, 2s, 4s, ... max from config). If persistent, check physical network.

### Transaction buffer empty despite completed fuellings

**Symptom:** `FpSupTrans_resp` returns empty buffer.

**Diagnosis:**
1. Check pump state via `FpStatus_req` — must be `EndOfTransaction` (5) for buffer to populate
2. Verify `configuredPumps` includes the pump number
3. Buffer may have been cleared by another POS

**Resolution:** Ensure only one POS is connected to the DOMS device. Check `configuredPumps` config.

### Volume/amount conversion incorrect

**Symptom:** Canonical transactions have wrong volume or amount values.

**Diagnosis:**
1. DOMS volumes are in **centilitres** (integer). Conversion: `cl * 10,000 = microlitres`
2. DOMS amounts use a **x10 factor** (integer). Conversion: `value * 10 = minor units`
3. Check raw payload in transaction buffer for actual values

**Resolution:** Verify currency configuration. Check if DOMS firmware uses non-standard unit factors.

---

## Radix FDC Issues

### Signature validation failed (RESP_CODE=251)

**Symptom:** All requests return RESP_CODE=251 (signature error).

**Diagnosis:**
1. Verify `sharedSecret` matches the FDC's configured signing password
2. Check for whitespace differences in the signing input
3. SHA-1 computation order: inner content first, then wrap with signature

**Resolution:** Update `sharedSecret` in Portal. Test with the Radix VirtualLab simulator using known test vectors.

### Token mismatch (RESP_CODE=253)

**Symptom:** Transaction fetch returns RESP_CODE=253 (invalid token).

**Diagnosis:**
1. Token counter should increment 0-65535 with wrapping
2. Request/ACK pair must use the same TOKEN value
3. FDC may have restarted, resetting its token counter

**Resolution:** Adapter auto-retries with a fresh token. If persistent, reset mode via CMD_CODE=20.

### No transactions despite completed fuellings

**Symptom:** FIFO buffer empty (RESP_CODE=205).

**Diagnosis:**
1. Check operating mode — must be ON_DEMAND (mode 1) for polling
2. If UNSOLICITED (mode 2), transactions are pushed, not polled
3. Verify the FDC is in the correct mode: `CMD_CODE=20, MODE=1`

**Resolution:** Ensure `ensureModeAsync(1)` runs before fetching. Check FDC firmware version >= 3.49.

### Dual-port confusion

**Symptom:** Requests sent to wrong port get no response.

**Diagnosis:**
1. **Auth port (P):** Pre-auth requests (`AUTH_DATA` XML)
2. **Transaction port (P+1):** CMD_CODE=10/20/55/201 requests
3. Verify `authPort` in config; transaction port = authPort + 1

**Resolution:** Check port configuration. Common pattern: auth=5000, transaction=5001.

### Pre-auth ACKCODE=258 (pump not ready)

**Symptom:** Pre-auth returns ACKCODE=258 (DSB offline or pump not ready).

**Diagnosis:**
1. Check if the pump is physically ready (nozzle lifted, pump idle)
2. Verify pump address mapping: canonical pump -> (PUMP_ADDR, FP)
3. Check `fccPumpAddressMap` in config

**Resolution:** Verify physical pump state. Confirm pump address mapping.

---

## Petronite Issues

### OAuth2 token acquisition failed

**Symptom:** 401 on all API calls, logs show OAuth token failure.

**Diagnosis:**
1. Verify `clientId` and `clientSecret` in site config
2. Check `oauthTokenEndpoint` URL is reachable
3. Verify the Petronite bot is running and accepting OAuth requests

**Resolution:** Update credentials in Portal. Test with `curl -X POST -H "Authorization: Basic <base64>" <tokenEndpoint>`.

### Webhook listener failed to start

**Symptom:** Log shows `HttpListenerException` on webhook port.

**Diagnosis:**
1. Check if port is already in use: `netstat -an | findstr :<port>`
2. On Windows, URL ACL may be needed: `netsh http add urlacl url=http://+:8090/api/webhook/petronite/ user=Everyone`
3. Verify `webhookListenerPort` in config (default: 8090)

**Resolution:** Free the port or change `PetroniteWebhookListenerPort` in appsettings.json. Grant URL ACL on Windows.

### Webhooks not arriving

**Symptom:** Webhook listener is running but no transactions received.

**Diagnosis:**
1. Verify the Petronite bot has the correct callback URL configured
2. Check `webhookSecret` matches between agent and Petronite bot
3. Verify network path: Petronite bot -> agent LAN IP:port
4. Check firewall allows inbound HTTP on the webhook port

**Resolution:** Ensure the Petronite bot is configured to POST to `http://<agent-ip>:<port>/api/webhook/petronite`. Verify secret header.

### Nozzle resolution failed

**Symptom:** Log shows `nozzle reverse-map failed` during normalization.

**Diagnosis:**
1. Nozzle resolver may not have initialized yet (first heartbeat)
2. Petronite nozzle ID doesn't match any cached assignment
3. Nozzle assignments may have changed (periodic refresh every 30 min)

**Resolution:** Wait for resolver initialization. Check `GET /nozzles/assigned` on the Petronite bot matches expected pump/nozzle layout.

### Pre-auth 400 (nozzle not lifted)

**Symptom:** Authorize step returns HTTP 400.

**Diagnosis:**
1. Petronite requires the nozzle to be physically lifted before authorization
2. Create order succeeds, but authorize fails if nozzle isn't ready

**Resolution:** Ensure the fuelling workflow lifts the nozzle before requesting pre-auth. Consider retry logic with a short delay.

### Stale orders on startup

**Symptom:** Multiple old pre-auth orders found during startup reconciliation.

**Diagnosis:**
1. Agent was down while pre-auth orders were created
2. Orders older than 30 minutes are auto-cancelled during reconciliation
3. Recent orders are re-adopted into the active pre-auth map

**Resolution:** This is normal behavior. Check logs for reconciliation summary: `"N cancelled, M adopted"`.

---

## Cloud Backend Issues

### Ingestion returns 409 CONFLICT

**Symptom:** Transaction upload returns 409.

**Diagnosis:** Duplicate `fccTransactionId` detected. This is expected behavior — the cloud deduplicates by FCC transaction ID.

**Resolution:** No action needed. The transaction was already processed.

### Radix XML ingress returns invalid signature

**Symptom:** Cloud XML endpoint rejects incoming Radix XML.

**Diagnosis:**
1. Verify `sharedSecret` and `usnCode` match between edge agent and cloud FCC config
2. Check `X-Usn-Code` header is present in the request

**Resolution:** Update cloud FCC config via Portal to match the edge agent's configuration.

---

## VirtualLab Simulator Testing

Use VirtualLab simulators to validate adapter behavior in a controlled environment:

1. **DOMS:** `POST /api/doms-jpl/inject-transaction` to add test transactions, `/set-pump-state` to control states
2. **Radix:** `POST /api/radix/inject-transaction` for FIFO buffer, `/set-mode` for ON_DEMAND/UNSOLICITED
3. **Petronite:** `POST /api/petronite/inject-transaction` for webhook flow, `/set-nozzle-state` for pump control

Error injection is available on all simulators to test failure handling.
