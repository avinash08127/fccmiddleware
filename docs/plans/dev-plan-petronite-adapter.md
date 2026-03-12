# Petronite FCC Adapter — Phased Development Plan

**Agent System Prompt:** Always prepend `docs/plans/agent-prompt-petronite-adapter.md` when assigning any task below.

**Reference Document:** `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — the Petronite protocol deep dive and integration analysis. Every task below references sections of that document.

**Sprint Cadence:** 2-week sprints

---

## Phase 0 — Shared Infrastructure & Config Changes (Sprint 1)

### PN-0.1: FccConnectionConfig & Enum Extensions

**Sprint:** 1
**Prereqs:** None
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §3.3 (what needs to be modified), §3.4 (configuration changes), §2.2 (OAuth2 credentials)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/Enums.cs` — current `FccVendor` enum (only has `Doms`)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` — current `FccConnectionConfig`, `PreAuthCommand`

**Task:**
Extend the shared adapter infrastructure to support Petronite-specific configuration: OAuth2 credentials and customer data fields.

**Detailed instructions:**
1. Add `Petronite` to the edge `FccVendor` enum in `Enums.cs`
2. Extend `FccConnectionConfig` with new fields needed by Petronite:
   - `ClientId` (string?) — OAuth2 client ID for Petronite bot authentication
   - `ClientSecret` (string?) — OAuth2 client secret for Petronite bot authentication
   - `WebhookSecret` (string?) — shared secret for validating incoming webhook calls (if Petronite supports signing — see PQ-3)
   - `OAuthTokenEndpoint` (string?) — override for token endpoint path (default: `/oauth/token`)
3. If not already present from Radix adapter work (RX-0.1), extend `PreAuthCommand` with:
   - `CustomerTaxId` (string?) — maps to Petronite `customerId`
   - `CustomerName` (string?) — maps to Petronite `customerName`
4. All new fields on `FccConnectionConfig` must be nullable/optional so that existing DOMS (and Radix) configurations are unaffected
5. `ClientId` and `ClientSecret` MUST be treated as sensitive — ensure they follow the same encrypted-at-rest pattern as `ApiKey` (REQ-3 BR-3.5)

**Acceptance criteria:**
- `FccVendor.Petronite` is available in the edge enum
- `FccConnectionConfig` has OAuth2 credential fields as optional properties
- `PreAuthCommand` has customer data fields (all nullable)
- Existing DOMS adapter code compiles and all tests pass without changes
- No breaking changes to any existing adapter interface or shared type

---

### PN-0.2: Petronite Adapter Factory Registration

**Sprint:** 1
**Prereqs:** PN-0.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs` — current factory (switch on `FccVendor`)
- `src/cloud/FccMiddleware.Infrastructure/Adapters/FccAdapterFactory.cs` — cloud factory (registry-based pattern)
- `src/cloud/FccMiddleware.Domain/Enums/FccVendor.cs` — cloud enum (already has `PETRONITE`)

**Task:**
Register the Petronite adapter in both edge and cloud adapter factories.

**Detailed instructions:**
1. In the edge `FccAdapterFactory.Create()`, add a case for `FccVendor.Petronite` → `new PetroniteAdapter(...)`. For now, throw `NotImplementedException` until the class exists in Phase 1.
2. In the cloud `FccAdapterFactory`, register the `PetroniteCloudAdapter` for `FccVendor.PETRONITE` (stub with `NotImplementedException` until PN-5.1)
3. Verify the cloud `FccVendor` enum already has `PETRONITE` — if not, add it
4. Add the new Petronite project references to the factory projects' `.csproj` files once the projects exist (Phase 1 and Phase 5)

**Acceptance criteria:**
- Edge factory switch includes `Petronite` vendor
- Cloud factory registry includes `PETRONITE` vendor
- Attempting to create a Petronite adapter throws `NotImplementedException` (temporary)
- Existing DOMS path is unaffected
- Unit test verifies factory recognizes `FccVendor.Petronite`

---

## Phase 1 — Core Adapter Skeleton (Sprints 1–2)

### PN-1.1: Petronite Directory Structure & Project Scaffold

**Sprint:** 1
**Prereqs:** PN-0.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §3.2 (new files list)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/` — reference directory structure to follow

**Task:**
Create the Petronite adapter directory structure and empty class files following the established DOMS/Radix pattern.

**Detailed instructions:**
1. Create directory `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Petronite/`
2. Create empty/stub files:
   - `PetroniteAdapter.cs` — class implementing `IFccAdapter` with all methods throwing `NotImplementedException`
   - `PetroniteProtocolDtos.cs` — placeholder for JSON DTOs
   - `PetroniteOAuthClient.cs` — placeholder for OAuth2 token lifecycle management
   - `PetroniteNozzleResolver.cs` — placeholder for nozzle ID mapping and caching
3. Create test directory `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Petronite/`
4. Create empty test files:
   - `PetroniteAdapterTests.cs`
   - `PetroniteOAuthClientTests.cs`
   - `PetroniteNozzleResolverTests.cs`
5. Create test fixtures directory `src/desktop-edge-agent/tests/FccDesktopAgent.Core.Tests/Adapter/Petronite/Fixtures/`
6. Ensure the project compiles with all stubs

**Acceptance criteria:**
- Directory structure matches the DOMS/Radix pattern
- All stub classes compile
- `PetroniteAdapter` implements `IFccAdapter` with `NotImplementedException` stubs
- `dotnet build` succeeds with zero errors

---

### PN-1.2: OAuth2 Client Credentials — PetroniteOAuthClient

**Sprint:** 1–2
**Prereqs:** PN-1.1
**Estimated effort:** 1.5–2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.2 (OAuth2 authentication details, token TTL, refresh strategy), §9.4 (OAuth2 token lifecycle)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs` — reference for HTTP client patterns (named client `"fcc"`)

**Task:**
Implement the OAuth2 token lifecycle manager. This is the most foundational Petronite-specific building block — every other API call depends on a valid bearer token.

**Detailed instructions:**
1. Create `PetroniteOAuthClient` with the following API:
   - `Task<string> GetAccessTokenAsync(CancellationToken ct)` — returns a cached, valid bearer token. Transparently acquires or refreshes as needed.
   - `void InvalidateToken()` — clears the cached token (called on 401 responses to force re-authentication)

2. **Token acquisition:**
   - `POST http://{host}:{port}/oauth/token` (or `OAuthTokenEndpoint` if overridden)
   - `Content-Type: application/x-www-form-urlencoded`
   - `Authorization: Basic <Base64(clientId:clientSecret)>`
   - Body: `grant_type=client_credentials`
   - Parse response: `{ "access_token": "...", "token_type": "bearer", "expires_in": 43199, "scope": "read write" }`

3. **Token caching:**
   - Store access token and its expiry time (`DateTimeOffset.UtcNow + TimeSpan.FromSeconds(expires_in)`)
   - Return cached token if remaining TTL > refresh threshold (configurable, default: 10 minutes)

4. **Proactive refresh:**
   - When remaining TTL < refresh threshold, acquire a new token before the old one expires
   - Return the old token to the caller while refresh is in-flight (don't block waiting for refresh)

5. **Thread safety:**
   - Use a `SemaphoreSlim(1,1)` to ensure only one token acquisition/refresh is in-flight at a time
   - Multiple concurrent callers waiting for a token should all receive the same result once the single refresh completes

6. **Reactive refresh (401 handling):**
   - When the adapter receives a 401 from any API call, it calls `InvalidateToken()` and retries once with a fresh token
   - This handles the bot-restart scenario where existing tokens are invalidated server-side

7. **Error handling:**
   - Token endpoint unreachable → throw `FccAdapterException(IsRecoverable: true)` (transient)
   - Token endpoint returns 401 → throw `FccAdapterException(IsRecoverable: false)` (bad credentials — config issue)
   - Token endpoint returns 500 → throw `FccAdapterException(IsRecoverable: true)` (transient)

**Unit tests (in `PetroniteOAuthClientTests.cs`):**
- Acquire token on first call → HTTP POST made, token returned
- Second call within TTL → no HTTP call, cached token returned
- Call when TTL < threshold → new token acquired proactively
- Concurrent calls → only one HTTP request made (thread safety)
- `InvalidateToken()` → next call acquires a new token
- Token endpoint returns 401 → non-recoverable exception
- Token endpoint unreachable → recoverable exception
- Token endpoint returns invalid JSON → non-recoverable exception

**Acceptance criteria:**
- Token acquired via standard OAuth2 Client Credentials flow
- Token cached and reused within TTL
- Proactive refresh before expiry (within threshold window)
- Thread-safe — no duplicate acquisition under concurrency
- `InvalidateToken()` forces re-authentication on next call
- Error types correctly classified as recoverable/non-recoverable
- All unit tests pass

---

### PN-1.3: Petronite Protocol DTOs — PetroniteProtocolDtos

**Sprint:** 2
**Prereqs:** PN-1.1
**Estimated effort:** 0.5–1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.2 (token response), §2.3 (nozzle assignment), §2.4 (create/authorize requests and responses), §2.6 (webhook payload), §2.8 (pending orders), §2.9 (order details), §2.10 (cancellation), §2.11 (error format), Appendix D (response wrapper format)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsProtocolDtos.cs` — DOMS DTO pattern to follow

**Task:**
Create all Petronite-specific JSON data transfer objects covering every API request, response, and webhook payload.

**Detailed instructions:**
1. Create the following record types in `PetroniteProtocolDtos.cs`:

   **OAuth:**
   - `PetroniteTokenResponse` — `AccessToken` (string), `TokenType` (string), `ExpiresIn` (int), `Scope` (string)

   **Nozzle discovery:**
   - `PetronitePumpInfo` — `Id` (long), `PumpName` (string), `PumpNumber` (int)
   - `PetroniteNozzleInfo` — `Id` (long), `Name` (string), `Grade` (string)
   - `PetroniteNozzleAssignment` — `Pump` (PetronitePumpInfo), `Nozzles` (List<PetroniteNozzleInfo>)

   **Pre-auth (create order):**
   - `PetroniteCreateOrderRequest` — `CustomerName` (string), `CustomerId` (string), `Type` (string = "PUMA_ORDER"), `NozzleId` (long), `AuthorizeType` (string = "Amount"), `Dose` (decimal?)
   - `PetroniteCreateOrderResponse` — `Data` (PetroniteOrderData?), `Message` (string), `Errors` (List<PetroniteFieldError>?)
   - `PetroniteOrderData` — `Id` (long), `CustomerName` (string?), `Status` (string), `Type` (string), `PumpAuthorizeType` (string?), `CreatedAt` (string?)

   **Pre-auth (authorize pump):**
   - `PetroniteAuthorizeRequest` — `RequestId` (long), `TruckNumber` (string?)
   - `PetroniteAuthorizeResponse` — `Data` (PetroniteOrderData?), `Message` (string), `Errors` (List<PetroniteFieldError>?)

   **Cancellation:**
   - `PetroniteCancelResponse` — `Data` (PetroniteOrderData?), `Message` (string), `Errors` (List<PetroniteFieldError>?)

   **Pending orders:**
   - `PetronitePendingOrdersResponse` — `Data` (List<PetronitePendingOrder>?), `Message` (string), `Errors` (List<PetroniteFieldError>?)
   - `PetronitePendingOrder` — `Id` (long), `CustomerName` (string?), `Status` (string), `Type` (string), `Nozzle` (PetroniteNozzleInfo?), `PumpAuthorizeType` (string?), `Dose` (decimal?), `CreatedAt` (string?)

   **Webhook:**
   - `PetroniteWebhookPayload` — `Event` (string), `Data` (PetroniteTransactionData)
   - `PetroniteTransactionData` — `Id` (long), `Volume` (decimal), `Price` (decimal), `Amount` (decimal), `Pump` (int), `Nozzle` (long), `Grade` (string), `CustomerId` (string?), `CustomerName` (string?), `Day` (string), `Hour` (string), `ReceiptCode` (string?), `PaymentMethod` (string?), `TruckNumber` (string?)

   **Shared error type:**
   - `PetroniteFieldError` — `Field` (string), `DefaultMessage` (string)

2. All DTOs should be C# `record` types (immutable)
3. Use `System.Text.Json` serialization attributes (`[JsonPropertyName]`) for snake_case field names where Petronite uses them (e.g., `receipt_code`, `customer_id`, `customer_name`, `payment_method`, `truck_number` in webhook)
4. Use `decimal` for all monetary/volume values — conversion to `long` minor units/microlitres happens during normalization, not in DTOs

**Acceptance criteria:**
- All DTOs cover every field from every Petronite API endpoint and webhook payload
- DTOs are immutable records
- JSON property names match Petronite's actual field names (snake_case where applicable)
- Monetary/volume fields remain as `decimal` (no premature conversion)
- Compiles cleanly with no warnings

---

### PN-1.4: Nozzle ID Resolution — PetroniteNozzleResolver

**Sprint:** 2
**Prereqs:** PN-1.2, PN-1.3
**Estimated effort:** 1–1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.3 (nozzle discovery API, pump addressing comparison), §9.3 (nozzle ID resolution strategy — Option C recommended)

**Task:**
Implement the nozzle ID resolver that maps our canonical pump/nozzle numbers to Petronite's internal nozzle IDs. This is critical because Petronite's `POST /create` endpoint requires the Petronite-internal `nozzleId` (a Long, e.g., 123, 1204), not a simple nozzle number.

**Detailed instructions:**
1. Create `PetroniteNozzleResolver` with the following API:
   - `Task InitializeAsync(CancellationToken ct)` — fetch `GET /nozzles/assigned`, build mapping cache, validate against configured mapping
   - `long? ResolvePetroniteNozzleId(int fccPumpNumber, int fccNozzleNumber)` — returns the Petronite nozzle ID for a given canonical pump/nozzle pair, or null if not found
   - `int? ResolveCanonicalNozzleNumber(long petroniteNozzleId)` — reverse mapping: Petronite nozzle ID → canonical nozzle number (needed for webhook normalization)
   - `int? ResolveCanonicalPumpNumber(int petronitePumpNumber)` — Petronite pump number → canonical pump number
   - `Task RefreshAsync(CancellationToken ct)` — re-fetch from API and update cache

2. **Initialization (Option C from §9.3):**
   - Call `GET /nozzles/assigned` using the OAuth client for authentication
   - Parse the response into `List<PetroniteNozzleAssignment>`
   - Build two in-memory dictionaries:
     - Forward: `(fccPumpNumber, fccNozzleNumber) → petroniteNozzleId`
     - Reverse: `petroniteNozzleId → (canonicalPumpNumber, canonicalNozzleNumber)`
   - The forward mapping uses `fcc_pump_number` = Petronite `pump.pumpNumber` and `fcc_nozzle_number` = Petronite `nozzle.id` (per §9.3, we store Petronite nozzle IDs in `fcc_nozzle_number`)
   - Validate the fetched nozzle assignments against the configured nozzle mapping (from site config). If a mismatch is detected (nozzle ID in config doesn't match API), log a WARNING with details. Do not fail — use the API-fetched data as authoritative.

3. **Periodic refresh:**
   - Configurable interval (default: 30 minutes)
   - Nozzle assignments may change daily (Petronite documentation suggests nozzles are "assigned" for the day)
   - Refresh silently in background; if refresh fails, keep using the last successful data

4. **Thread safety:**
   - Use `ReaderWriterLockSlim` or immutable snapshot pattern — reads are concurrent, writes (refresh) are exclusive

**Unit tests (in `PetroniteNozzleResolverTests.cs`):**
- Initialize with mock nozzle assignment response → forward and reverse mappings correct
- Resolve known pump/nozzle → correct Petronite nozzle ID returned
- Resolve unknown pump/nozzle → returns null
- Reverse resolve: Petronite nozzle ID → correct canonical pump/nozzle
- Refresh updates the mapping with new data
- Mismatch between config and API → warning logged but API data used
- API failure during refresh → stale data retained, warning logged
- Thread-safety: concurrent reads during refresh don't crash

**Acceptance criteria:**
- Nozzle assignments fetched from `GET /nozzles/assigned` on initialization
- Forward mapping resolves canonical pump/nozzle → Petronite nozzle ID
- Reverse mapping resolves Petronite nozzle ID → canonical pump/nozzle
- Config vs API mismatch produces a warning (not a failure)
- Periodic refresh updates the cache
- Thread-safe for concurrent reads
- All unit tests pass

---

### PN-1.5: Heartbeat Implementation

**Sprint:** 2
**Prereqs:** PN-1.2, PN-1.4
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §8 (heartbeat strategy — `GET /nozzles/assigned` recommended)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs` — DOMS `HeartbeatAsync` for reference (timeout/error handling pattern)

**Task:**
Implement `PetroniteAdapter.HeartbeatAsync()` using the nozzle assignment endpoint as a liveness probe. This confirms the Petronite bot application layer is healthy AND validates the OAuth2 token.

**Detailed instructions:**
1. In `PetroniteAdapter`, implement `HeartbeatAsync(CancellationToken)`:
   - Get bearer token via `PetroniteOAuthClient.GetAccessTokenAsync(ct)`
   - `GET http://{host}:{port}/nozzles/assigned` with `Authorization: Bearer {token}`
   - If HTTP 200 → return `true` (optionally trigger a nozzle resolver refresh with the response data)
   - If HTTP 401 → call `oauthClient.InvalidateToken()`, retry once with fresh token
   - If retry also fails → return `false`
2. Apply the IFccAdapter heartbeat contract: 5-second hard timeout, never throw on unreachability — return `false`
3. Catch and log transport errors (network, timeout) — return `false`
4. Catch and log 401 with failed retry — return `false` but log as WARNING (credentials may be wrong)
5. Use the named `HttpClient` from `IHttpClientFactory` (client name: `"fcc"`, same as DOMS)

**Unit tests (in `PetroniteAdapterTests.cs`):**
- Successful heartbeat (mock returns 200 with nozzle data) → returns `true`
- Bot unreachable (mock throws `HttpRequestException`) → returns `false`
- Timeout (mock throws `TaskCanceledException`) → returns `false`
- Token expired (401) then retry succeeds (200) → returns `true`, token refreshed
- Token expired (401) and retry also fails (401) → returns `false`, warning logged
- Invalid response body (parse error) → returns `true` (we got 200; body doesn't matter for heartbeat)

**Acceptance criteria:**
- Heartbeat uses `GET /nozzles/assigned`
- Returns `true` only on successful HTTP 200
- Transparently handles 401 with one retry (token refresh)
- Never throws — always returns `bool`
- 5-second timeout enforced
- All unit tests pass

---

## Phase 2 — Transaction Normalization (Sprints 2–3)

### PN-2.1: Webhook Payload Normalization — NormalizeAsync

**Sprint:** 2–3
**Prereqs:** PN-1.3, PN-1.4
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §4 (complete field mapping table), §2.7 (transaction ID / dedup key), §2.12 (fiscal data), §9.6 (normal order vs pre-auth detection)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/CanonicalTransaction.cs` — target canonical model
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Doms/DomsAdapter.cs` — DOMS `NormalizeAsync` for reference pattern

**Task:**
Implement `PetroniteAdapter.NormalizeAsync()` — parse a Petronite webhook JSON payload and map it to a `CanonicalTransaction`.

**Detailed instructions:**
1. In `NormalizeAsync(RawPayloadEnvelope envelope, CancellationToken ct)`:
   - Deserialize JSON from `envelope.RawJson` into `PetroniteWebhookPayload`
   - Validate `Event == "transaction_completed"` — if not, throw `FccAdapterException("Unexpected event type", IsRecoverable: false)`
   - If `Data` is null, throw `FccAdapterException("No transaction data in webhook", IsRecoverable: false)`

2. **Field mappings (from §4):**
   - `FccTransactionId` = `"{data.Id}"` (string representation of Petronite's numeric ID)
   - `SiteCode` = from `envelope.SiteCode` (injected from config)
   - `PumpNumber` = resolve `data.Pump` via nozzle resolver: Petronite pump number → canonical pump number. If 1:1, use directly. Log warning if mapping not found.
   - `NozzleNumber` = resolve `data.Nozzle` (Petronite nozzle ID, which is a Long) via nozzle resolver's reverse mapping: `petroniteNozzleId → canonicalNozzleNumber`. **This is the critical mapping.** Log warning if not found; use raw value as fallback.
   - `ProductCode` = `data.Grade` (e.g., "PMS", "AGO"). Petronite appears to use standard fuel grade codes. If a `productCodeMapping` exists in config, apply it; otherwise use the grade string directly.
   - `VolumeMicrolitres` = `(long)(data.Volume * 1_000_000m)` — Petronite provides volume in litres with up to 2 decimal places
   - `AmountMinorUnits` = `(long)(data.Amount * 100m)` — assuming major currency units per PQ-1. **Must be confirmed.** Use configurable `currencyDecimalPlaces` from site config (default: 2 for ZMW).
   - `UnitPriceMinorPerLitre` = `(long)(data.Price * 100m)` — same currency conversion
   - `CompletedAt` = parse `data.Day` + `data.Hour` as `"yyyy-MM-dd"` + `"HH:mm:ss"`, apply configured timezone → UTC `DateTimeOffset`
   - `StartedAt` = same as `CompletedAt` (Petronite only provides one timestamp — per §4, use for both or leave `StartedAt` null)
   - `FiscalReceiptNumber` = `data.ReceiptCode` (direct mapping; null/empty → null)
   - `FccVendor` = `"PETRONITE"` (hardcoded)
   - `AttendantId` = `null` (not provided by Petronite)
   - `SchemaVersion` = `"1.0"`

3. **Volume conversion precision:** Use `decimal` arithmetic: `(long)(decimal.Parse(vol.ToString()) * 1_000_000m)` to avoid floating-point precision loss

4. **Payment method detection (§9.6):** Check `data.PaymentMethod`:
   - `"PUMA_ORDER"` → this is a pre-auth (POS-controlled) dispense. The reconciliation engine should attempt pre-auth matching.
   - Any other value → this is a Normal Order. Store `payment_method` in the raw payload for downstream logic.
   - Note: The canonical model doesn't have a `paymentMethod` field — this info is preserved in the raw payload.

5. **Timezone handling:** The adapter must know the site's timezone to convert `Day + Hour` to UTC. Default to UTC if not configured, with a warning log.

**Unit tests (in `PetroniteAdapterTests.cs`):**
- Normalize standard webhook → all fields mapped correctly
- Volume conversion: `25.50` → `25_500_000L`
- Amount conversion (ZMW, 2 decimal places): `71400.00` → `7_140_000L`
- Price conversion: `2800.00` → `280_000L`
- Cross-check: `volume * price ≈ amount` (sanity validation)
- FccTransactionId: `15001` → `"15001"`
- Fiscal receipt: `"RCPT-987654"` → `FiscalReceiptNumber`
- Null `receipt_code` → null `FiscalReceiptNumber`
- Timestamp conversion with configured timezone (e.g., Africa/Lusaka UTC+2)
- Nozzle reverse mapping: Petronite nozzle ID `123` → canonical nozzle number
- Missing nozzle mapping → fallback to raw value, warning logged
- Payment method `"PUMA_ORDER"` → preserved in raw payload
- Unexpected event type → throws non-recoverable exception

**Acceptance criteria:**
- All §4 field mappings implemented correctly
- Dedup key composed from Petronite `id`
- Volume in microlitres (long), amount in minor units (long) — no floating point
- Nozzle ID reverse-mapped through nozzle resolver
- Timezone conversion applied
- Fiscal receipt extracted
- Payment method preserved for reconciliation
- All unit tests pass

---

### PN-2.2: FetchTransactionsAsync — Push-Only Stub

**Sprint:** 2
**Prereqs:** PN-1.1
**Estimated effort:** 0.25 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §9.2 (no pull mode — ingestion constraints)

**Task:**
Implement `PetroniteAdapter.FetchTransactionsAsync()` as a no-op that returns an empty batch. Petronite has no documented pull API — it is strictly push-only via webhooks.

**Detailed instructions:**
1. Return `Task.FromResult(new TransactionBatch(Array.Empty<RawPayloadEnvelope>(), NextCursor: null, HasMore: false))`
2. Log at DEBUG level: "Transaction fetch not supported by Petronite — push-only via webhook"
3. The adapter metadata should report `SupportedIngestionMethods = [PUSH]`

**Acceptance criteria:**
- Returns empty `TransactionBatch` without contacting the Petronite bot
- `HasMore = false` always
- No exceptions thrown
- Debug log message present

---

### PN-2.3: GetPumpStatusAsync — Synthesized Status

**Sprint:** 3
**Prereqs:** PN-1.4
**Estimated effort:** 0.5–1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §3.1 (`GetPumpStatusAsync` — synthesized from nozzle assignments + pending orders), §2.8 (pending orders API)

**Task:**
Implement `PetroniteAdapter.GetPumpStatusAsync()` with synthesized pump status from nozzle assignment data and pending order state. Petronite has no real-time dispensing status endpoint.

**Detailed instructions:**
1. Use the cached nozzle assignments from `PetroniteNozzleResolver` to get the list of active pumps
2. Optionally call `GET /direct-authorize-requests/pending` to identify pumps that have active PUMA locks:
   - If a pending order exists for a pump → pump state = `PumpState.Authorized`
   - Otherwise → pump state = `PumpState.Idle` (best guess — we can't know if it's actually dispensing a normal order)
3. Return a list of `PumpStatus` for each known pump
4. Set `PumpStatusSource = PumpStatusSource.EdgeSynthesized` (not `FccLive`)
5. If the nozzle resolver has no data or the pending orders call fails, return an empty list

**Acceptance criteria:**
- Returns pump list based on cached nozzle assignments
- Pumps with pending PUMA orders show as `Authorized`
- Pumps without pending orders show as `Idle`
- Status source is `EdgeSynthesized` (not `FccLive`)
- Failures return empty list without throwing

---

## Phase 3 — Pre-Authorization: Two-Step Flow (Sprints 3–4)

### PN-3.1: SendPreAuthAsync — Step 1 (Create Order)

**Sprint:** 3
**Prereqs:** PN-1.2, PN-1.3, PN-1.4
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.4 (two-step pre-auth flow, create order request/response), §5 (PreAuthCommand → create order field mapping), §6 (response mapping), §9.1 (architectural decision — Option A recommended)
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Common/AdapterTypes.cs` — `PreAuthCommand`, `PreAuthResult`

**Task:**
Implement `PetroniteAdapter.SendPreAuthAsync()` performing Step 1 only (Create Order) per the Option A recommendation. This locks the pump/nozzle and returns the Petronite order ID as the correlation ID. Step 2 (Authorize) is handled separately.

**Detailed instructions:**
1. **Nozzle ID resolution:**
   - Use `PetroniteNozzleResolver.ResolvePetroniteNozzleId(command.FccPumpNumber, command.FccNozzleNumber)` to get the Petronite-internal nozzle ID
   - If null (nozzle not found in mapping), return `PreAuthResult(Accepted: false, ErrorCode: "NOZZLE_MAPPING_NOT_FOUND", ErrorMessage: "Petronite nozzle ID not found for pump {p} nozzle {n}")`

2. **Build create order request (per §5):**
   - `customerName` = `command.CustomerName` (or fallback to a default if null)
   - `customerId` = `command.CustomerTaxId` (Petronite requires this field)
   - `type` = `"PUMA_ORDER"` (hardcoded — the POS-controlled authorization workflow)
   - `nozzleId` = resolved Petronite nozzle ID (Long)
   - `authorizeType` = `"Amount"` (we always authorize by amount per BR-6.1b)
   - `dose` = `command.RequestedAmountMinorUnits / 100.0m` — convert from minor units to major currency units (Petronite expects major units per PQ-1). Use configurable `currencyDecimalPlaces`.

3. **Send request:**
   - Get bearer token via `PetroniteOAuthClient.GetAccessTokenAsync(ct)`
   - `POST http://{host}:{port}/direct-authorize-requests/create`
   - `Authorization: Bearer {token}`
   - `Content-Type: application/json`
   - If 401 → invalidate token, retry once
   - Parse `PetroniteCreateOrderResponse`

4. **Map response to PreAuthResult (per §6):**
   - HTTP 200 + `data.Status == "STARTED"` → `PreAuthResult(Accepted: true, FccCorrelationId: data.Id.ToString(), FccAuthorizationCode: null, ErrorCode: null, ErrorMessage: null)`
   - HTTP 200 + `errors` non-null or `data` null → `PreAuthResult(Accepted: false, ErrorCode: "CREATE_FAILED", ErrorMessage: concatenated error messages)`
   - HTTP 400 → `PreAuthResult(Accepted: false, ErrorCode: "BAD_REQUEST", ErrorMessage: response.Message)` (non-recoverable — bad input)
   - HTTP 404 → `PreAuthResult(Accepted: false, ErrorCode: "NOZZLE_NOT_FOUND", ErrorMessage: response.Message)` (non-recoverable — nozzle ID doesn't exist in Petronite)
   - HTTP 500 → `PreAuthResult(Accepted: false, ErrorCode: "FCC_INTERNAL_ERROR", ErrorMessage: response.Message)` (recoverable)
   - Network error → `PreAuthResult(Accepted: false, ErrorCode: "FCC_UNREACHABLE")`

5. **Important:** Per Appendix D, always check both HTTP status AND the `errors` array in the response wrapper — some errors may return HTTP 200 with an error payload.

6. **Track active pre-auth:** Store the mapping `petroniteOrderId → { preAuthId, pumpNumber, nozzleNumber, createdAt }` in an internal `ConcurrentDictionary`. This is needed for:
   - Step 2 (Authorize) to know which order to authorize
   - Cancellation to know which order to cancel
   - Webhook correlation (the Petronite `id` in the webhook links back to this order)

**Unit tests:**
- Successful create order → `Accepted = true`, `FccCorrelationId` = order ID string
- Nozzle mapping not found → `Accepted = false`, `NOZZLE_MAPPING_NOT_FOUND`
- Petronite returns 400 (bad request) → `Accepted = false`, `BAD_REQUEST`
- Petronite returns 404 → `Accepted = false`, `NOZZLE_NOT_FOUND`
- Petronite returns 500 → `Accepted = false`, `FCC_INTERNAL_ERROR`
- Petronite returns 200 with `errors` array → `Accepted = false`, `CREATE_FAILED`
- Network error → `Accepted = false`, `FCC_UNREACHABLE`
- OAuth token expired (401) → retries once with fresh token
- Dose calculation correct: minor units 7140000 → major units 71400.00 (for ZMW)
- Customer data included in request when present
- Order tracked in internal active pre-auth map

**Acceptance criteria:**
- Create Order sent to correct endpoint with correct JSON body
- Nozzle ID resolved from canonical pump/nozzle to Petronite ID
- Amount converted from minor units to major currency units
- All error codes mapped correctly per §6
- OAuth 401 handled with retry
- Active pre-auth tracked for Step 2 and cancellation
- All unit tests pass

---

### PN-3.2: Authorize Pump — Step 2

**Sprint:** 3–4
**Prereqs:** PN-3.1
**Estimated effort:** 1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.4 (authorize pump request/response, nozzle-lift precondition), §9.1 (two-step handling — Option A vs B), Appendix B (order status lifecycle)

**Task:**
Implement the Step 2 (Authorize Pump) capability. Per the Option A recommendation, this is NOT called from within `SendPreAuthAsync` — it is exposed as a separate Petronite-specific method that the Edge Agent's pre-auth handler invokes when the attendant signals readiness (nozzle lifted).

**Detailed instructions:**
1. Add a method to `PetroniteAdapter` (not part of `IFccAdapter` — Petronite-specific):
   ```
   Task<PreAuthResult> AuthorizePumpAsync(string fccCorrelationId, string? truckNumber, CancellationToken ct)
   ```

2. **Implementation:**
   - Look up `fccCorrelationId` (which is the Petronite order ID string) in the active pre-auth map
   - If not found, return `PreAuthResult(Accepted: false, ErrorCode: "ORDER_NOT_FOUND")`
   - Build `PetroniteAuthorizeRequest`: `RequestId = long.Parse(fccCorrelationId)`, `TruckNumber = truckNumber`
   - `POST http://{host}:{port}/direct-authorize-requests/authorize` with bearer token
   - Parse `PetroniteAuthorizeResponse`

3. **Response mapping:**
   - HTTP 200 + `data.Status == "COMPLETED"` → `PreAuthResult(Accepted: true, FccCorrelationId: fccCorrelationId)` — pump is now authorized and dispensing
   - HTTP 400 (nozzle not lifted) → `PreAuthResult(Accepted: false, ErrorCode: "NOZZLE_NOT_LIFTED", ErrorMessage: "Nozzle must be lifted before authorization")` — recoverable, attendant needs to lift nozzle
   - HTTP 404 → `PreAuthResult(Accepted: false, ErrorCode: "ORDER_NOT_FOUND")` — order may have been cancelled or timed out
   - Other errors → map as in PN-3.1

4. **Retry option (Option B fallback):** If Option B is preferred later, add a polling wrapper:
   - Retry the authorize call every 2 seconds for up to 60 seconds (configurable)
   - On each 400 (nozzle not lifted), wait and retry
   - On success or non-retryable error, stop
   - This can be added as a configuration toggle without changing the core method

5. **Expose via adapter interface consideration:** Since `IFccAdapter` doesn't have a `ConfirmPreAuth` method, the Edge Agent's PreAuth handler must type-check the adapter or use a Petronite-specific code path. Document this clearly.

**Unit tests:**
- Authorize succeeds (nozzle lifted) → `Accepted = true`
- Authorize fails (nozzle not lifted, 400) → `Accepted = false`, `NOZZLE_NOT_LIFTED`
- Order not found in active map → `Accepted = false`, `ORDER_NOT_FOUND`
- Petronite returns 404 → `Accepted = false`
- OAuth token handling (401 retry)
- Polling retry (if implemented): retry on 400, succeed on 3rd attempt

**Acceptance criteria:**
- Authorize Pump sends correct JSON to correct endpoint
- Nozzle-not-lifted error correctly identified and reported as recoverable
- Order ID resolved from correlation ID
- OAuth 401 handled with retry
- Method accessible to Edge Agent's pre-auth handler

---

### PN-3.3: Pre-Auth Cancellation — CancelPreAuthAsync

**Sprint:** 4
**Prereqs:** PN-3.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.10 (cancellation API), Appendix B (status lifecycle — only STARTED orders can be cancelled)

**Task:**
Implement `PetroniteAdapter.CancelPreAuthAsync()` — cancel a pending PUMA order.

**Detailed instructions:**
1. `fccCorrelationId` is the Petronite order ID string
2. `POST http://{host}:{port}/direct-authorize-requests/{id}/cancel` with bearer token
3. Parse `PetroniteCancelResponse`
4. HTTP 200 + `data.Status == "CANCELLED"` → remove from active pre-auth map, return `true`
5. HTTP 404 → order already completed or doesn't exist, return `true` (idempotent per IFccAdapter contract)
6. HTTP 400 → order may already be authorized/dispensing (cannot cancel), return `false`
7. Network error → return `false`
8. Remove the order from the active pre-auth map on success or 404

**Unit tests:**
- Cancel STARTED order → success, removed from active map
- Cancel already-completed order (404) → true (idempotent)
- Cancel during dispensing (400) → false
- Order not found in active map → still attempt cancellation (order may have been created in a previous session)
- Network error → false

**Acceptance criteria:**
- Cancel sends POST to correct endpoint with order ID
- Idempotent — already-completed returns true
- Cannot cancel during dispensing — returns false
- Active pre-auth map cleaned up on success
- OAuth 401 handled

---

### PN-3.4: Startup Reconciliation — Pending Order Recovery

**Sprint:** 4
**Prereqs:** PN-3.1, PN-3.3
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.8 (pending orders API), §13 (acceptance criteria: orphaned pending orders)

**Task:**
Implement startup reconciliation that detects and handles orphaned PUMA orders from a previous adapter session (e.g., after Edge Agent crash).

**Detailed instructions:**
1. On adapter initialization (after OAuth client and nozzle resolver are ready), call `GET /direct-authorize-requests/pending`
2. Parse `PetronitePendingOrdersResponse`
3. For each pending order with `status == "STARTED"`:
   - Check `createdAt` — if older than configurable threshold (default: 30 minutes), auto-cancel via `POST /{id}/cancel`
   - If within threshold, add to the active pre-auth map for potential future authorize/cancel
   - Log each pending order at INFO level
4. Publish a structured log event summarizing: `{totalPending, autoCancelled, readopted}`
5. If the pending orders call fails (network, auth), log WARNING and continue — non-fatal

**Unit tests:**
- Startup finds 2 pending orders: 1 stale (>30 min) + 1 recent → stale auto-cancelled, recent re-adopted
- Startup finds 0 pending orders → no action, info logged
- Pending orders API fails → warning logged, adapter continues normally
- Auto-cancel of stale order succeeds → order removed
- Auto-cancel of stale order fails → warning logged, continues

**Acceptance criteria:**
- Stale pending orders auto-cancelled on startup
- Recent pending orders re-adopted into active pre-auth map
- API failure during startup is non-fatal
- Structured logging for audit trail

---

### PN-3.5: Pre-Auth ↔ Dispense Correlation via Petronite Order ID

**Sprint:** 4
**Prereqs:** PN-3.1, PN-2.1
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.7 (dedup key), §9.6 (PUMA_ORDER detection)
- Appendix C (Petronite status → our pre-auth state mapping)

**Task:**
Implement correlation between pre-auth orders and their resulting dispense transactions when webhook payloads arrive.

**Detailed instructions:**
1. In `NormalizeAsync`, after parsing the webhook:
   - Check `data.PaymentMethod`:
     - If `"PUMA_ORDER"` → this is a pre-auth dispense. Look up `data.Id` (or the related order ID if different from transaction ID) in the active pre-auth map.
     - If found → set `CanonicalTransaction.FccCorrelationId` = the Petronite order ID, and populate `OdooOrderId` from the stored pre-auth mapping
     - Remove from active map (pre-auth is now completed)
   - If not `"PUMA_ORDER"` → Normal Order, no correlation attempt

2. **Note on ID linking:** The webhook `data.id` may be the transaction ID (distinct from the order ID returned by Create Order). Need to confirm whether the webhook echoes the `requestId` from Step 1. If not, correlation may need to use `pump + nozzle + time window` instead. Document this uncertainty.

3. If the webhook's transaction cannot be linked to an active pre-auth (e.g., the adapter restarted between pre-auth and dispense), the `FccCorrelationId` is set to the transaction ID and `OdooOrderId` remains null — the cloud reconciliation engine handles unmatched cases.

**Acceptance criteria:**
- PUMA_ORDER transactions linked to active pre-auths when possible
- Non-PUMA_ORDER transactions skip correlation
- Active pre-auth map cleaned up after correlation
- Unlinked PUMA_ORDER transactions still get a `FccCorrelationId` for later reconciliation

---

## Phase 4 — Push Mode: Webhook Endpoints (Sprints 4–5)

### PN-4.1: Edge Agent Webhook Listener

**Sprint:** 4–5
**Prereqs:** PN-2.1
**Estimated effort:** 2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.6 (webhook payload format), §7.2 (edge agent webhook reception), §9.5 (webhook authentication)

**Task:**
Implement an HTTP endpoint in the Edge Agent that accepts Petronite webhook callbacks (transaction_completed events). This is the primary transaction ingestion path for Petronite sites.

**Detailed instructions:**
1. Create a webhook endpoint handler (on the Edge Agent's LAN-accessible HTTP server):
   - Route: `POST /api/webhook/petronite` (or a configurable path)
   - Accept `Content-Type: application/json`

2. **Request processing pipeline:**
   a. **Authentication** (per §9.5 and PQ-3 resolution):
      - If `webhookSecret` is configured, validate `X-Webhook-Secret` header matches
      - Optionally validate source IP against configured allowlist (LAN IPs)
      - If validation fails, return 401
   b. Deserialize JSON body into `PetroniteWebhookPayload`
   c. Validate event type = `"transaction_completed"` — ignore other event types with 200 (don't reject — Petronite might send other events in future)
   d. Validate required fields: `data.id`, `data.volume`, `data.amount`, `data.pump`, `data.nozzle`, `data.grade`, `data.day`, `data.hour` — if missing, log error and return 200 (don't reject — we received the event, it's just malformed)
   e. Wrap in `RawPayloadEnvelope` and feed into the ingestion pipeline (buffer manager / ingestion orchestrator)
   f. Return HTTP 200 immediately (don't wait for processing)

3. **Why return 200 even on internal errors:** The Petronite bot considers HTTP 200 as acknowledgment. If we return a non-200, the bot's retry behavior is undocumented (PQ-2). Returning 200 and buffering the raw payload for internal retry is safer than risking duplicate webhooks.

4. **Port configuration:** The webhook listener should be on a separate port accessible from the Petronite bot's LAN IP (e.g., configurable `PetroniteWebhookListenerPort`, default: 8586). It must NOT be `localhost`-only — the bot needs to reach it.

5. Wire the webhook listener into the Edge Agent's DI and startup lifecycle.

**Unit tests:**
- Valid webhook payload → 200, transaction queued for processing
- Missing `X-Webhook-Secret` when configured → 401
- Wrong `X-Webhook-Secret` → 401
- No webhook secret configured → any source accepted (200)
- Non-`transaction_completed` event → 200, ignored with debug log
- Malformed payload (missing required fields) → 200, error logged
- Internal processing error → 200 still returned (webhook acknowledged)

**Acceptance criteria:**
- HTTP listener accepts Petronite JSON webhook POSTs
- Authentication validated when configured
- Transactions parsed and fed into ingestion pipeline
- HTTP 200 returned for all valid-looking requests
- Invalid payloads logged but not rejected (return 200)
- Listener accessible from LAN (not localhost-only)

---

### PN-4.2: Ingestion Mode Validation

**Sprint:** 5
**Prereqs:** PN-2.2
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §9.2 (no pull mode — ingestion constraints)

**Task:**
Add configuration validation that rejects invalid ingestion modes for Petronite sites.

**Detailed instructions:**
1. In the adapter initialization or config validation layer:
   - If `FccVendor == Petronite` and `IngestionMethod == PULL`, reject with clear error: "Petronite does not support pull mode. Use PUSH or HYBRID."
   - If `FccVendor == Petronite` and `IngestionMethod == HYBRID`, log WARNING: "Petronite has no pull API. HYBRID will function as PUSH-only (no catch-up polling)."
   - If `FccVendor == Petronite` and `IngestionMode == CloudDirect`, log WARNING: "Petronite bot is typically LAN-only. Verify bot can reach cloud endpoint."
2. Adapter metadata reports `SupportedIngestionMethods = [PUSH]`

**Acceptance criteria:**
- `PULL` mode rejected for Petronite
- `HYBRID` warning logged (functions as PUSH-only)
- `CLOUD_DIRECT` warning logged
- Adapter metadata accurately reports PUSH-only

---

## Phase 5 — Cloud Adapter (Sprints 5–6)

### PN-5.1: Cloud Adapter Project Scaffold

**Sprint:** 5
**Prereqs:** CB-1.1 (cloud adapter interface exists)
**Estimated effort:** 0.5 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §3.2 (cloud backend new files)
- `src/cloud/FccMiddleware.Adapter.Doms/` — reference project structure
- `src/cloud/FccMiddleware.Domain/Interfaces/IFccAdapter.cs` — cloud adapter interface

**Task:**
Create the Petronite cloud adapter project structure.

**Detailed instructions:**
1. Create `src/cloud/FccMiddleware.Adapter.Petronite/` project:
   - `FccMiddleware.Adapter.Petronite.csproj` — reference `FccMiddleware.Domain`
   - `PetroniteCloudAdapter.cs` — stub implementing cloud `IFccAdapter`
   - `Internal/PetroniteWebhookDto.cs` — webhook JSON DTOs (can share/copy from edge DTOs)
   - `Internal/PetroniteWebhookValidator.cs` — validate incoming webhook payloads
2. Create `src/cloud/FccMiddleware.Adapter.Petronite.Tests/` project:
   - `PetroniteNormalizationTests.cs`
   - `PetroniteValidationTests.cs`
3. Add project reference in `FccMiddleware.sln`
4. Register in cloud `FccAdapterFactory` for `FccVendor.PETRONITE`

**Acceptance criteria:**
- Project compiles and is referenced in the solution
- Factory resolves `FccVendor.PETRONITE` → `PetroniteCloudAdapter`
- Stub methods throw `NotImplementedException`

---

### PN-5.2: Cloud Adapter — NormalizeTransaction & ValidatePayload

**Sprint:** 5–6
**Prereqs:** PN-5.1
**Estimated effort:** 1.5 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §4 (field mapping), §2.7 (dedup key), §2.12 (fiscal data)
- `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs` — DOMS cloud normalization for reference

**Task:**
Implement `PetroniteCloudAdapter.NormalizeTransaction()` and `ValidatePayload()` for the cloud side.

**Detailed instructions:**
1. **`ValidatePayload(RawPayloadEnvelope)`:**
   - Check `envelope.Vendor == FccVendor.PETRONITE`
   - Check payload is non-null/non-empty
   - Attempt JSON parse into `PetroniteWebhookPayload` — if invalid JSON, return `ValidationResult(isValid: false, errorCode: "INVALID_JSON", recoverable: false)`
   - Check `Event == "transaction_completed"` — if not, return `ValidationResult(isValid: false, errorCode: "UNEXPECTED_EVENT_TYPE")`
   - Check required fields: `data.Id`, `data.Volume`, `data.Amount`, `data.Pump`, `data.Nozzle`, `data.Grade`, `data.Day`, `data.Hour` — if missing, return `ValidationResult(isValid: false, errorCode: "MISSING_REQUIRED_FIELD", message: "Field {name} is required")`
   - Return `ValidationResult(isValid: true)` on success

2. **`NormalizeTransaction(RawPayloadEnvelope)`:**
   - Same field mapping logic as edge `NormalizeAsync` (§4 mapping table)
   - Use `SiteFccConfig` for pump/nozzle mappings, product code mappings, timezone, and currency decimal places
   - Compose `fccTransactionId` = `"{data.Id}"`
   - Apply all conversions: volume → microlitres, amount → minor units, timestamp → UTC
   - Map `receipt_code` → `fiscalReceiptNumber`

3. **Code reuse consideration:** The JSON parsing and field mapping logic is identical between edge and cloud. Consider extracting shared normalization logic into a shared class. For MVP, acceptable to duplicate.

4. **`GetAdapterMetadata()`:**
   ```csharp
   return new AdapterInfo(
       Vendor: FccVendor.PETRONITE,
       AdapterVersion: "1.0.0",
       SupportedTransactionModes: [IngestionMethod.PUSH],
       SupportsPreAuth: true,
       SupportsPumpStatus: false,
       Protocol: "REST/JSON");
   ```

**Unit tests:**
- Validate well-formed webhook JSON → valid
- Validate malformed JSON → invalid with `INVALID_JSON`
- Validate wrong event type → invalid with `UNEXPECTED_EVENT_TYPE`
- Validate missing required fields → invalid with `MISSING_REQUIRED_FIELD`
- Normalize all fields correctly (same test vectors as PN-2.1)
- Normalize with nozzle ID → canonical nozzle number mapping
- Normalize with timezone conversion
- Adapter metadata reports correct capabilities

**Acceptance criteria:**
- Validation catches structural issues before normalization
- Normalization produces correct `CanonicalTransaction` matching edge adapter output
- All mapped fields match §4 specification
- Adapter metadata accurate (PUSH-only, pre-auth yes, pump status no)

---

### PN-5.3: Cloud Webhook Ingress Endpoint

**Sprint:** 6
**Prereqs:** PN-5.2, CB-1.2 (ingestion pipeline exists)
**Estimated effort:** 1.5–2 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §7.1 (cloud webhook ingress), §9.5 (webhook authentication)
- `schemas/openapi/cloud-api.yaml` — existing ingestion endpoint

**Task:**
Create the cloud-side webhook endpoint that accepts Petronite `transaction_completed` events directly from the Petronite bot (for `CLOUD_DIRECT` mode) or forwarded from the Edge Agent.

**Detailed instructions:**
1. Create endpoint: `POST /api/v1/ingest/petronite/webhook`
   - Accept `Content-Type: application/json`

2. **Authentication (per §9.5):**
   - Validate webhook source:
     - If `webhookSecret` is configured for the site, check `X-Webhook-Secret` header
     - IP allowlisting as additional layer if configured
   - Alternative: detect site from a header or query parameter (e.g., `X-Site-Code` or `?site=XXX`) and look up the webhook secret from the site's `fcc_configs`

3. **Processing pipeline:**
   a. Identify the site (from header, query param, or payload context)
   b. Resolve the Petronite cloud adapter for the site
   c. Validate payload via `PetroniteCloudAdapter.ValidatePayload()`
   d. Normalize via `PetroniteCloudAdapter.NormalizeTransaction()`
   e. Feed into existing dedup → store → outbox pipeline (CB-1.2)
   f. Return HTTP 200 (simple JSON: `{ "status": "ok" }`)

4. **Unlike Radix, no special response format is needed** — Petronite expects a standard HTTP 200.

5. **Error handling:**
   - Authentication failure → 401
   - Validation failure → 200 (acknowledge receipt, log error internally — see PN-4.1 rationale)
   - Internal processing error → 200 (acknowledge receipt, queue for retry)

**Acceptance criteria:**
- Cloud endpoint accepts Petronite JSON webhook payloads
- Webhook authentication validated
- Transaction normalized and stored via standard pipeline
- HTTP 200 returned to Petronite bot
- Authentication failure → 401
- Invalid payloads acknowledged (200) but error logged

---

## Phase 6 — VirtualLab Simulation (Sprint 6)

### PN-6.1: VirtualLab Petronite Bot Simulator Profile

**Sprint:** 6
**Prereqs:** PN-2.1, PN-3.1, PN-3.2
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §2.2–§2.10 (all API endpoints), Appendix A (endpoint reference), Appendix B (order lifecycle)
- `VirtualLab/` — existing VirtualLab project structure and seed profiles

**Task:**
Create a Petronite bot simulator profile in VirtualLab for end-to-end testing.

**Detailed instructions:**
1. Add a `petronite-like` profile to `SeedProfileFactory` (or equivalent VirtualLab configuration)
2. The simulator must run on a single port and expose:

   **OAuth2:**
   - `POST /oauth/token` — accept client credentials, return a mock access token with configurable expiry

   **Nozzle discovery:**
   - `GET /nozzles/assigned` — return a pre-configured list of pump/nozzle assignments

   **Pre-auth flow:**
   - `POST /direct-authorize-requests/create` — validate nozzleId exists, create a pending order, return order ID with status STARTED
   - `POST /direct-authorize-requests/authorize` — validate requestId exists and is STARTED. Configurable: either succeed immediately (nozzle lifted) or fail with 400 (nozzle not lifted) for N attempts before succeeding.
   - `POST /direct-authorize-requests/{id}/cancel` — cancel if STARTED, return error if already COMPLETED
   - `GET /direct-authorize-requests/pending` — return list of STARTED orders
   - `GET /direct-authorize-requests/{id}/details` — return order details

   **Webhook simulation:**
   - After a successful authorize, automatically POST a `transaction_completed` webhook to a configured callback URL after a configurable delay (simulating dispense time)
   - The webhook payload should include realistic data: volume, amount, price, receipt_code, etc.

3. Seed data: pre-configure 3–4 pumps with 2 nozzles each (PMS, AGO), configurable prices

4. **Configurable error injection:**
   - OAuth: return 401 to simulate expired credentials
   - Create: return 400/404 to simulate bad nozzle
   - Authorize: return 400 for configurable number of attempts (simulating nozzle-not-lifted delay)
   - Webhook: configurable delay or failure (simulate webhook delivery issues)

**Acceptance criteria:**
- Simulator responds to all Petronite API endpoints with correct JSON structure
- OAuth2 flow works (token acquisition, bearer validation on subsequent calls)
- Two-step pre-auth flow works end-to-end: create → authorize → webhook
- Cancellation works
- Nozzle discovery returns correct data
- Error injection produces realistic error responses
- End-to-end test: Edge Agent configured with Petronite adapter → connects to simulator → creates pre-auth → receives webhook → normalizes transaction

---

## Phase 7 — Integration Testing & Hardening (Sprint 7)

### PN-7.1: End-to-End Integration Tests

**Sprint:** 7
**Prereqs:** All previous phases
**Estimated effort:** 2–3 days

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §13 (acceptance criteria checklist)

**Task:**
Create comprehensive integration tests covering the full Petronite adapter flow.

**Detailed instructions:**
1. **OAuth2 E2E test:**
   - Adapter starts → acquires token → makes API call → success
   - Token expires → adapter transparently refreshes → call succeeds
   - Bot restarts (simulated via 401) → adapter re-authenticates → call succeeds

2. **Nozzle discovery E2E test:**
   - Adapter initializes → fetches nozzle assignments → mapping populated
   - Pre-auth with mapped nozzle → correct Petronite nozzle ID used
   - Nozzle mapping mismatch → warning logged

3. **Two-step pre-auth E2E test:**
   - Send pre-auth via `SendPreAuthAsync` → Create Order succeeds → `Accepted = true`
   - Call `AuthorizePumpAsync` → pump authorized
   - Webhook arrives → transaction normalized, correlated to pre-auth

4. **Webhook E2E test (edge):**
   - Start webhook listener
   - Simulate Petronite webhook POST → transaction received, parsed, buffered
   - Verify canonical transaction fields are correct

5. **Cloud webhook E2E test:**
   - POST Petronite webhook to cloud ingestion endpoint
   - Transaction stored, HTTP 200 returned

6. **Pre-auth cancellation E2E test:**
   - Create order → cancel order → verify cancelled
   - Attempt to authorize cancelled order → fails

7. **Startup reconciliation E2E test:**
   - Seed mock with pending orders → adapter starts → stale orders auto-cancelled

8. **Error handling tests:**
   - OAuth credential failure → non-recoverable error
   - Bot unreachable → graceful degradation
   - Nozzle not found → pre-auth fails with clear error
   - Nozzle not lifted → authorize fails with retryable error

9. **Verify all §13 acceptance criteria** as a checklist

**Acceptance criteria:**
- All §13 acceptance criteria verified programmatically
- OAuth2 lifecycle works across token refresh and bot restart
- Two-step pre-auth works end-to-end
- Webhook reception and normalization works
- Pre-auth ↔ dispense correlation works
- Startup reconciliation handles orphaned orders
- Error handling is robust across all failure modes

---

### PN-7.2: Documentation & Open Question Resolution

**Sprint:** 7
**Prereqs:** All implementation phases
**Estimated effort:** 1 day

**Read these artifacts before starting:**
- `docs/FCCAdapters/Petronite/WIP-PetroniteFCCAdapterPlan.md` — §10 (open questions PQ-1 through PQ-10), §12 (risk register)

**Task:**
Document final decisions and resolve or escalate remaining open questions.

**Detailed instructions:**
1. Update the WIP plan document with:
   - Resolution status for each open question (PQ-1 through PQ-10)
   - Decisions made during implementation for §9 architectural choices
   - Any deviations from the original plan
2. Document the Petronite adapter's configuration requirements
3. Document the nozzle ID mapping strategy with examples
4. Document the two-step pre-auth flow for operators:
   - How to configure the Petronite bot's webhook URL
   - How the attendant workflow changes for PUMA orders
   - How to troubleshoot common issues (nozzle not lifted, OAuth failures)
5. Add operational notes:
   - How to configure a new Petronite site
   - Default ingestion mode recommendation (RELAY)
   - How to monitor webhook delivery health

**Acceptance criteria:**
- All open questions have a resolution or documented escalation
- Configuration format documented with examples
- Attendant workflow documented
- Operational runbook for Petronite sites
- WIP plan updated to reflect actual implementation

---

## Dependency Graph

```
Phase 0: PN-0.1 → PN-0.2
              ↓
Phase 1: PN-1.1 → PN-1.2 ──→ PN-1.4 → PN-1.5
              ↓                  ↑
           PN-1.3 ───────────────┘

Phase 2: PN-2.1 ←── PN-1.3, PN-1.4
           PN-2.2     (independent, trivial)
           PN-2.3 ←── PN-1.4

Phase 3: PN-3.1 ←── PN-1.2, PN-1.3, PN-1.4
              ↓
           PN-3.2 ←── PN-3.1
           PN-3.3 ←── PN-3.1
           PN-3.4 ←── PN-3.1, PN-3.3
           PN-3.5 ←── PN-3.1, PN-2.1

Phase 4: PN-4.1 ←── PN-2.1
           PN-4.2 ←── PN-2.2

Phase 5: PN-5.1 ←── CB-1.1
              ↓
           PN-5.2 ←── PN-5.1
              ↓
           PN-5.3 ←── PN-5.2, CB-1.2

Phase 6: PN-6.1 ←── PN-2.1, PN-3.1, PN-3.2

Phase 7: PN-7.1 ←── All previous
           PN-7.2 ←── All previous
```

---

## Risk Register (from WIP plan §12)

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| **Webhook delivery failure = lost transactions** — No pull API means no catch-up. | Medium | **High** — data loss | Use RELAY mode (LAN webhook → Edge Agent) as default. Confirm retry behaviour (PQ-2). Request pull API (PQ-6). |
| **Two-step pre-auth timing — nozzle not lifted** — Authorize fails if attendant is slow. | High | Medium — poor UX, stuck orders | Retry logic with timeout. UI affordance in Odoo POS. Auto-cancel stale orders (PN-3.4). |
| **Normal Orders not sent via webhook** — Petronite may only webhook PUMA_ORDER transactions. | Medium | **High** — majority of transactions invisible | Confirm PQ-7. If excluded, Petronite sites need alternative strategy. |
| **OAuth2 token invalidation on bot restart** — Tokens invalidated without standard expiry. | Medium | Low — brief interruption | 401 → re-authenticate. Minimal disruption (PN-1.2). |
| **Nozzle ID changes after bot reconfiguration** | Low | Medium — wrong nozzle mapping | Startup validation (PN-1.4). Alert on mismatch. |
| **Currency format ambiguity** — Wrong amount/price conversion corrupts financial data. | Medium | **High** — financial corruption | Confirm PQ-1. Add sanity-check: `volume × price ≈ amount`. |
| **Petronite bot is LAN-only — CLOUD_DIRECT not feasible** | High | Medium — must use RELAY | Default to RELAY (PN-4.2). Document in deployment guide. |
| **Webhook endpoint spoofing** — Without auth, fake transactions possible. | Low (LAN) / Medium (cloud) | High — fake data | Implement webhook authentication (PQ-3). IP allowlisting for LAN. |

---

## Prerequisites Checklist

Before implementation begins:

- [ ] **PQ-1 resolved** — Currency/amount format confirmed (Critical for PN-2.1)
- [ ] **PQ-2 resolved** — Webhook retry behaviour confirmed (Critical for reliability strategy)
- [ ] **PQ-4 resolved** — Bot internet connectivity confirmed (Determines default ingestion mode)
- [ ] **PQ-7 resolved** — Normal Order webhook behaviour confirmed (Critical — determines if Petronite sites can ingest Normal Orders)
- [ ] **PQ-3 resolved** — Webhook authentication mechanism confirmed (Important for cloud security)
- [ ] **Access to a real Petronite bot** (or test instance) for integration testing
- [ ] **Petronite test credentials** — Client ID and secret for test environment
- [ ] **Cloud backend Phase 1 complete** (CB-1.1, CB-1.2) — needed for PN-5.x tasks
