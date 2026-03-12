# FCC Adapter Codebase Scan — Full Findings Report

**Scan Date:** 2026-03-13
**Scope:** All FCC adapter implementations across Android Edge Agent, Desktop Edge Agent, Cloud Layer, and Portal Layer
**Total Issues Found:** 63

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [End-to-End Transaction Lifecycle Gaps](#end-to-end-transaction-lifecycle-gaps)
3. [Critical Issues (P0)](#critical-issues-p0)
4. [High-Severity Issues (P1)](#high-severity-issues-p1)
5. [Medium-Severity Issues (P2)](#medium-severity-issues-p2)
6. [Low-Severity Issues (P3)](#low-severity-issues-p3)
7. [Cross-Layer Consistency Issues](#cross-layer-consistency-issues)
8. [Remediation Priority Matrix](#remediation-priority-matrix)

---

## Executive Summary

| Layer | Critical | High | Medium | Low | Total |
|-------|----------|------|--------|-----|-------|
| **Android Edge Agent** | 1 | 4 | 7 | 3 | 15 |
| **Desktop Edge Agent** | 0 | 3 | 10 | 3 | 16 |
| **Cloud Layer** | 3 | 4 | 6 | 7 | 20 |
| **Portal Layer** | 2 | 3 | 4 | 3 | 12 |
| **Totals** | **6** | **14** | **27** | **16** | **63** |

**Key Risk Areas:**
- **Data Loss:** Advatec multi-item receipts silently drop items 2+ (Cloud); Advatec config fields lost on portal edit
- **Financial Integrity:** Currency factor mapping gaps across all layers; inconsistent across adapters
- **Silent Failures:** Timestamp fallback to UtcNow, XML parsing swallowed, webhook startup failure ignored
- **Missing Lifecycle States:** No FAILED/DEAD_LETTER state for permanently rejected transactions
- **Security:** Bootstrap token TOCTOU race, Advatec webhook token full-table scan DOS vector

---

## End-to-End Transaction Lifecycle Gaps

### Expected Lifecycle

```
FCC Device → Edge Adapter (parse) → Normalize → Buffer (PENDING)
  → Cloud Upload (UPLOADED) → Cloud Adapter (normalize) → Persist
  → Odoo Sync (SYNCED_TO_ODOO) → Archive (ARCHIVED)
```

### Identified Semantic Gaps

#### GAP-1: No FAILED/DEAD_LETTER Terminal State
- **Layer:** Android Edge, Desktop Edge
- **Problem:** Transactions that permanently fail cloud upload (after max retries) remain PENDING forever. No mechanism to move them out of the active lifecycle.
- **Impact:** Failed records pollute buffer metrics, block dedup, and accumulate indefinitely.
- **Missing Action:** Need a DEAD_LETTER state after max retry exhaustion, with separate cleanup policy.

#### GAP-2: No Timeout for UPLOADED Records
- **Layer:** Android Edge, Desktop Edge
- **Problem:** Records marked UPLOADED but never reaching SYNCED_TO_ODOO have no TTL or timeout mechanism.
- **Impact:** Records stuck in UPLOADED accumulate indefinitely; no visibility; no alerting.
- **Missing Action:** Need a TTL-based transition from UPLOADED → STALE after configurable duration.

#### GAP-3: Missing Pre-Auth Cancellation Propagation
- **Layer:** Android Edge, Cloud
- **Problem:** When a pre-auth is cancelled locally, there's no guaranteed propagation to cloud. PreAuthCloudForwardWorker forwards active pre-auths but cancellation events may be lost.
- **Missing Action:** Need explicit cancellation event forwarding to cloud + Odoo.

#### GAP-4: No Transaction Receipt Confirmation Loop
- **Layer:** Edge → Cloud
- **Problem:** After cloud upload returns success, there's no callback/acknowledgement loop. If cloud persists but returns timeout, edge retries and cloud must handle dedup.
- **Impact:** Edge cannot distinguish "cloud received" from "cloud timed out but received." Relies entirely on cloud-side dedup.

#### GAP-5: Pre-Auth to Transaction Matching Has No Cross-Adapter Consistency
- **Layer:** All adapters
- **Problem:** Radix uses token-based matching, Petronite uses orderId, Advatec uses pumpNumber. Each adapter defines its own pre-auth lifecycle independently. No common pre-auth matching interface.
- **Missing Action:** Standardize pre-auth matching contract across adapters.

#### GAP-6: Pump Status Is Non-Functional Across All Adapters
- **Layer:** Android Edge, Desktop Edge
- **Problem:** Every adapter returns empty or synthesized pump status. The `/pump-status` API endpoint exists but returns no real data.
- **Missing Action:** Either implement real pump status per adapter or formally deprecate the endpoint.

#### GAP-7: Fiscalization Receipt Flow Incomplete
- **Layer:** Desktop Edge
- **Problem:** IFiscalizationService interface exists but only Advatec implements it. No post-fiscalization confirmation or retry logic.
- **Missing Action:** Define retry/fallback behavior when fiscalization fails.

---

## Critical Issues (P0)

### C-01: Advatec Multi-Item Receipts Only Process First Item
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Adapter.Advatec/AdvatecCloudAdapter.cs` (lines 91-92, 132)
- **Problem:** Validation accepts `Items.Count > 0`, but NormalizeTransaction always processes only `receipt.Items![0]`, silently discarding items 2+. TRA receipts can contain multiple line items.
- **Impact:** **Revenue data loss**. All items except first are permanently discarded. Transaction totals incorrect. Product-level analytics inaccurate.

### C-02: Advatec Pump Mapping Completely Missing
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Adapter.Advatec/AdvatecCloudAdapter.cs` (line 162), `FccConfig.cs` (lines 54-58)
- **Problem:** Advatec receipts have no pump identification. Adapter hardcodes all transactions to pump `0 - offset`. No compensating pump mapping exists (unlike Radix's FccPumpAddressMap).
- **Impact:** All Advatec transactions from a site attributed to single pump. Multi-pump reconciliation broken. Inventory tracking per pump inaccurate.

### C-03: Petronite Timestamps Silently Fallback to UtcNow
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Adapter.Advatec/PetroniteCloudAdapter.cs` (lines 149-154)
- **Problem:** If StartTime or EndTime fail to parse, the adapter silently replaces them with `DateTimeOffset.UtcNow` (server processing time). No logging or validation occurs.
- **Impact:** Transactions placed in wrong time windows. Reconciliation fails. Historical reporting inaccurate. Pre-auth matching breaks.

### C-04: Portal Advatec Config Fields Lost on Site Edit
- **Layer:** Portal
- **File:** `src/portal/src/app/features/site-config/site-detail.component.ts` (lines 532-558)
- **Problem:** When entering edit mode, `draftFccConfig` is created WITHOUT Advatec-specific fields (`advatecDevicePort`, `advatecWebhookListenerPort`, `advatecWebhookToken`, `advatecEfdSerialNumber`, `advatecCustIdType`). Any save permanently deletes these values from the database.
- **Impact:** **Permanent data loss** of Advatec configuration on ANY routine edit. Sites become non-functional.

### C-05: Portal Catch-Up Pull Intervals Missing from Form
- **Layer:** Portal
- **File:** `src/portal/src/app/features/site-config/fcc-config-form.component.ts` (lines 20-50)
- **Problem:** `FccConfigDraft` type excludes `catchUpPullIntervalSeconds` and `hybridCatchUpIntervalSeconds`. No UI to view or configure these fields.
- **Impact:** Operations teams cannot optimize pull cadence for catch-up scenarios. Stale catch-up settings remain in production. Fields invisible to users.

### C-06: DOMS Adapter Is Non-Functional Stub (Android)
- **Layer:** Android Edge
- **File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/DomsAdapter.kt`
- **Problem:** Base DomsAdapter throws `UnsupportedOperationException` on all methods. Only `DomsJplAdapter` is usable, but factory doesn't prevent selection of base DomsAdapter.
- **Impact:** System claims to support DOMS but base adapter is non-functional. Runtime crash if selected.

---

## High-Severity Issues (P1)

### H-01: Radix Signature Validation Without Exact Whitespace Verification
- **Layer:** Cloud
- **Files:** `src/cloud/FccMiddleware.Adapter.Radix/RadixCloudAdapter.cs` (lines 140-157, 286-314)
- **Problem:** SHA1 signature extraction uses simple IndexOf/Substring without XML parsing. No verification that extracted content matches expected structure.
- **Impact:** Potential for malformed Radix XML to bypass signature validation. Debugging signature failures impossible.

### H-02: Bootstrap Token Limit TOCTOU Race
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Application/Registration/GenerateBootstrapTokenHandler.cs` (lines 38-44, 90-101)
- **Problem:** Pre-insert limit check (count >= 5) races with concurrent inserts. Post-insert detection only logs warning without rejecting. No DB constraint enforces limit.
- **Impact:** Unbounded bootstrap token growth under concurrent requests.

### H-03: HTTP Resilience Not Wired for Cloud Adapters
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Infrastructure/Adapters/CloudFccAdapterFactoryRegistration.cs` (lines 30, 42-59)
- **Problem:** Infrastructure provides retry + circuit breaker extensions, but CloudFccAdapterFactoryRegistration creates bare HttpClients with no resilience policies.
- **Impact:** No retries for transient HTTP failures. No circuit breaker. Thread pool exhaustion if downstream FCC is down.

### H-04: Advatec Webhook Token Lookup Full Table Scan (DOS Vector)
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs` (lines 120-165)
- **Problem:** `GetByAdvatecWebhookTokenAsync` loads ALL active Advatec configs into memory then compares tokens in-memory. No indexed lookup.
- **Impact:** Performance degrades linearly with tenant growth. Webhook endpoint becomes DOS vector.

### H-05: HttpClient Resource Leak in Desktop Advatec Adapter
- **Layer:** Desktop Edge
- **Files:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecApiClient.cs` (line 36), `FccAdapterFactory.cs` (lines 121-125)
- **Problem:** AdvatecApiClient creates raw `new HttpClient()` instead of using IHttpClientFactory. When cached adapters are replaced, HttpClient is never disposed.
- **Impact:** Process memory growth, socket exhaustion, port binding failures over time.

### H-06: Radix Push Listener Queue Overflow Race (Desktop)
- **Layer:** Desktop Edge
- **File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixPushListener.cs` (lines 346-354)
- **Problem:** Atomic increment-then-check allows concurrent requests to bypass MaxQueueSize limit under high load.
- **Impact:** Back-pressure protection ineffective. Potential OOM if FDC sends rapid push bursts.

### H-07: Webhook Listener Startup Failure Silently Ignored (Desktop)
- **Layer:** Desktop Edge
- **Files:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Ingestion/IngestionOrchestrator.cs` (lines 132-176)
- **Problem:** If webhook listener fails to bind port, exception is logged but `_initialized = true` is set unconditionally, preventing retry.
- **Impact:** Push-only adapters (Petronite, Advatec) silently fail to receive any data. No startup error visible.

### H-08: Portal SiteUsesPreAuth Missing from Model
- **Layer:** Portal
- **Files:** `src/portal/src/app/core/models/site.model.ts`
- **Problem:** Cloud API sends `SiteUsesPreAuth` in responses but portal model doesn't include it. UpdateSiteRequest also excludes it.
- **Impact:** Pre-auth enablement state invisible in portal. Cannot be toggled through UI.

### H-09: Portal Vendor-Specific Field Validation Missing
- **Layer:** Portal
- **File:** `src/portal/src/app/features/site-config/fcc-config-form.component.ts` (lines 618-630)
- **Problem:** Form validation only checks generic fields (vendor, host, port, heartbeat). No vendor-specific validation for DOMS (fcAccessCode), Radix (sharedSecret, usnCode), Petronite (clientId, clientSecret, OAuth URL), or Advatec fields.
- **Impact:** Invalid configurations deployed to agents. Runtime failures with unclear errors.

### H-10: Pre-Auth Memory Leak in All Android Adapters
- **Layer:** Android Edge
- **Files:** RadixAdapter.kt, PetroniteAdapter.kt, AdvatecAdapter.kt
- **Problem:** All three adapters maintain `activePreAuths` ConcurrentHashMap with inadequate cleanup. Radix has passive purge (only on heartbeat); Petronite/Advatec only purge in normalize() which may not run.
- **Impact:** Abandoned pre-auths accumulate in memory indefinitely. Can reach thousands of entries in high-throughput sites.

### H-11: Transaction Buffer Missing Terminal States (Android)
- **Layer:** Android Edge
- **File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/TransactionBufferManager.kt`
- **Problem:** Buffer states: PENDING → UPLOADED → SYNCED_TO_ODOO → ARCHIVED. No FAILED or DEAD_LETTER state. Permanently rejected transactions stay PENDING forever.
- **Impact:** Failed records accumulate indefinitely. No mechanism to segregate dead records from active ones.

### H-12: Radix Push Listener Silent Data Drop on Queue Overflow (Android)
- **Layer:** Android Edge
- **File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixPushListener.kt` (lines 91-137)
- **Problem:** Max queue overflow (10K transactions) silently drops data with no retry or persistence.
- **Impact:** Transactions permanently lost during high-volume push bursts.

### H-13: JplTcpClient Socket Leak (Android)
- **Layer:** Android Edge
- **File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/jpl/JplTcpClient.kt` (lines 78-108)
- **Problem:** Socket leak if `socketBinder` callback or `connect()` throws. No try-finally cleanup in connect().
- **Impact:** Socket descriptor exhaustion; connection failures cascade.

### H-14: Cloud Upload Batch Size Can Reach Zero
- **Layer:** Android Edge
- **File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudUploadWorker.kt` (line 143)
- **Problem:** `effectiveBatchSize` can be halved on 413 response with no lower bound. Repeated halving reaches 0.
- **Impact:** Upload stalls permanently. No recovery mechanism.

---

## Medium-Severity Issues (P2)

### M-01: Missing Configuration Validation at Cloud Adapter Resolution
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs` (lines 212-249)
- **Problem:** BuildSiteFccConfig populates all vendor-specific fields without validating required fields are present. Radix can have null UsnCode/AuthPort, DOMS null JplPort, etc.
- **Impact:** Silent misconfiguration. Runtime failures in adapters with no early warning.

### M-02: DOMS HTTP Response Body Exposed in Exception
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs` (lines 155-163)
- **Problem:** Non-2xx DOMS responses include full response body (512 chars) in exception messages, which are logged.
- **Impact:** Information disclosure — internal IPs, DB errors, stack traces may leak through logs.

### M-03: Radix Pump Address Map Parsing Fails Silently
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs` (lines 255-267)
- **Problem:** ParsePumpAddressMap returns null on malformed JSON with no logging. Adapter silently falls back to offset-based resolution.
- **Impact:** Pump mapping silently ineffective. Wrong pump numbers persisted.

### M-04: Inconsistent Currency Factor Mapping Across Cloud Adapters
- **Layer:** Cloud
- **Files:** AdvatecCloudAdapter.cs (lines 198-206), PetroniteCloudAdapter.cs (lines 175-183), RadixCloudAdapter.cs (lines 273-279)
- **Problem:** Each adapter has its own currency factor mapping with different gaps. Radix always uses 100; Advatec missing KRW; Petronite missing TZS/UGX/RWF. No centralized mapping.
- **Impact:** **100x monetary errors** for unmapped currencies across different adapters.

### M-05: Incomplete DOMS Timestamp Validation
- **Layer:** Cloud
- **File:** `src/cloud/FccMiddleware.Adapter.Doms/DomsCloudAdapter.cs` (lines 122-125)
- **Problem:** Validation only rejects `EndTime < StartTime` but allows zero-duration (`EndTime == StartTime`) transactions.
- **Impact:** Ambiguous transaction timestamps. Reconciliation edge cases.

### M-06: Inconsistent Null Handling Across Cloud Adapters
- **Layer:** Cloud
- **Files:** All cloud adapter ValidatePayload and NormalizeTransaction methods
- **Problem:** Adapters validate in ValidatePayload but throw InvalidOperationException in NormalizeTransaction if null. Race window between validate and normalize.
- **Impact:** Inconsistent error paths. Some errors bypass dead-letter queue.

### M-07: Radix Mode State Cache Can Become Stale (Desktop)
- **Layer:** Desktop Edge
- **File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixAdapter.cs` (lines 794-842)
- **Problem:** Mode change failures don't reset cached `_currentMode`. Next call skips CMD_CODE=20 even though FCC may be in different mode.
- **Impact:** Transaction fetches fail with mode mismatch errors. Difficult to recover without restart.

### M-08: Incomplete Transaction Lifecycle State Management (Desktop)
- **Layer:** Desktop Edge
- **Files:** `BufferedTransaction.cs` (lines 48-52), `RadixAdapter.cs` (line 486), `CloudUploadWorker.cs`
- **Problem:** Transactions have both Status and SyncStatus fields with unclear semantics. Status is set to "Pending" and never changed by any code path.
- **Impact:** Status field useless for diagnostics. No real lifecycle tracking.

### M-09: Silent XML Parsing Failures in Desktop RadixXmlParser
- **Layer:** Desktop Edge
- **File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixXmlParser.cs` (lines 43-84)
- **Problem:** XML parsing exceptions caught in broad `catch` blocks with no logging. Malformed FCC responses silently treated as "no transaction".
- **Impact:** Silent data loss on malformed XML. No visibility into FCC data quality issues.

### M-10: Missing Radix Configuration Validation (Desktop)
- **Layer:** Desktop Edge
- **File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/DesktopFccRuntimeConfiguration.cs` (lines 21-61)
- **Problem:** Radix requires SharedSecret, UsnCode, FccPumpAddressMap, AuthPort, but validation checks none.
- **Impact:** Invalid configs pass validation. Cryptic runtime errors instead of startup validation.

### M-11: Advatec Pre-Auth Race During Purge (Desktop)
- **Layer:** Desktop Edge
- **File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecAdapter.cs` (lines 104-105, 390-462)
- **Problem:** PurgeStalePreAuths and TryMatchPreAuth concurrently iterate `_activePreAuths` ConcurrentDictionary causing TOCTOU races.
- **Impact:** Pre-auth entries prematurely purged or skipped. Receipt correlation failures.

### M-12: Missing Advatec CancelPreAuth Correlation ID Validation (Desktop)
- **Layer:** Desktop Edge
- **File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecAdapter.cs` (lines 268-289)
- **Problem:** No format validation for correlation ID. Returns false for both "not found" and "invalid format" with no distinction.
- **Impact:** Silent cancellation failures. Uncanceled pre-auths persist until TTL.

### M-13: Unknown Currency Code Silently Defaults to Factor 100 (Desktop)
- **Layer:** Desktop Edge
- **File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecAdapter.cs` (lines 580-588)
- **Problem:** Hardcoded currency lookup (KWD/BHD/OMR=1000, JPY/KRW/TZS=1, default=100). Unknown currencies silently use 100.
- **Impact:** Financial data corruption for unsupported currencies. 10x or greater pricing errors.

### M-14: Inconsistent Timeout Configuration (Desktop)
- **Layer:** Desktop Edge
- **Files:** RadixAdapter.cs (line 85), AdvatecApiClient.cs (line 22), various
- **Problem:** All timeouts hardcoded (Pre-auth=30s, Advatec=10s, FiscalReceipt=30s, PumpStatus=1s). No configuration options.
- **Impact:** Operations fail on high-latency LANs. No environment-specific tuning.

### M-15: Radix Heartbeat Exception Resets Mode State Unnecessarily (Desktop)
- **Layer:** Desktop Edge
- **File:** `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixAdapter.cs` (line 756)
- **Problem:** Any heartbeat exception resets `_currentMode` to Unknown, causing unnecessary CMD_CODE=20 mode changes on transient failures.
- **Impact:** Redundant mode-change commands. Extra FCC chatter during transient issues.

### M-16: Portal Radix JSON Pump Map No Validation
- **Layer:** Portal
- **File:** `src/portal/src/app/features/site-config/fcc-config-form.component.ts` (lines 326-335)
- **Problem:** Pump address map textarea accepts arbitrary text. No JSON parsing validation. No schema validation.
- **Impact:** Malformed JSON deployed to agents. Errors only at agent runtime.

### M-17: Portal Bootstrap Token Exposed in DevTools
- **Layer:** Portal
- **File:** `src/portal/src/app/features/edge-agents/bootstrap-token.component.ts` (lines 385-390, 488-497)
- **Problem:** Full token persists in component signal memory. Accessible via browser DevTools. QR code contains raw token. No "copy & clear" mechanism.
- **Impact:** Token exposure if device is shared or screen captured.

### M-18: Portal Incomplete Model Mapping
- **Layer:** Portal
- **Files:** `site.model.ts`, `fcc-config-form.component.ts`, `site-detail.component.ts`
- **Problem:** Multiple fields exist in cloud API contracts but missing from portal: SiteUsesPreAuth, CatchUpPullIntervalSeconds, HybridCatchUpIntervalSeconds, all Advatec fields in draft.
- **Impact:** Fields silently dropped during save operations. Configuration parameters invisible.

### M-19: PreAuthHandler Nozzle Resolution After Insert (Android)
- **Layer:** Android Edge
- **File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/preauth/PreAuthHandler.kt` (line 181)
- **Problem:** Nozzle mapping resolved AFTER pre-auth record inserted. If mapping fails, orphaned record left in database with PENDING status.
- **Impact:** Orphaned pre-auth records. Database pollution.

### M-20: PreAuthCloudForwardWorker Null UnitPrice Stuck Records (Android)
- **Layer:** Android Edge
- **File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/PreAuthCloudForwardWorker.kt` (lines 126-135)
- **Problem:** Records with null unitPrice skipped indefinitely. No max attempt limit. Never forwarded to cloud.
- **Impact:** Odoo never sees these pre-auths. Orphaned records.

### M-21: Advatec Pre-Auth Overwrite Race (Android)
- **Layer:** Android Edge
- **File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/advatec/AdvatecAdapter.kt` (line 432)
- **Problem:** One pre-auth per pump: concurrent requests on same pump race without synchronization. First request's data silently lost.
- **Impact:** Lost pre-auth correlations. No cleanup notification.

### M-22: Inconsistent Timeout Values Across Android System
- **Layer:** Android Edge
- **Files:** RadixAdapter.kt, PetroniteAdapter.kt, AdvatecAdapter.kt, PreAuthHandler.kt
- **Problem:** Pre-auth: Radix 10s, Petronite 30s, Advatec 10s, Handler default 30s. No central configuration.
- **Impact:** Different adapters make different assumptions. Site-specific tuning difficult.

### M-23: ConfigManager URL Validation Incomplete (Android)
- **Layer:** Android Edge
- **File:** `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/ConfigManager.kt` (lines 112-120)
- **Problem:** HTTPS validation mentioned but unclear coverage of SSRF, port range, localhost blocking.
- **Impact:** Potential SSRF attack vector if URL validation incomplete.

---

## Low-Severity Issues (P3)

### L-01: Missing Petronite ProductCode Validation (Cloud)
- **Layer:** Cloud
- **File:** PetroniteCloudAdapter.cs (lines 143-146)
- **Problem:** No null check for ProductCode. Null product codes propagate to canonical transaction.

### L-02: DOMS Accepts Both Bare and Wrapped Array Shapes (Cloud)
- **Layer:** Cloud
- **File:** DomsCloudAdapter.cs (lines 207-226)
- **Problem:** Ambiguous API contract — accepts both `{transactionId}` and `{transactions: [{...}]}`.

### L-03: Missing Input Length Validation (Cloud)
- **Layer:** Cloud
- **Files:** All adapter ValidatePayload methods
- **Problem:** DB enforces max lengths (FccTransactionId 200 chars) but adapters don't validate pre-submission. Silent truncation.

### L-04: Missing Vendor Enum Validation at API Boundary (Cloud)
- **Layer:** Cloud
- **File:** CloudFccAdapterFactoryRegistration.cs (lines 18-26)
- **Problem:** New vendor enum values pass validation but factory.Resolve() fails with AdapterNotRegisteredException.

### L-05: Fuzzy Match Flag Lost in Dedup Race (Cloud)
- **Layer:** Cloud
- **File:** IngestTransactionHandler.cs (lines 144-163)
- **Problem:** Race condition on unique constraint: fuzzy match flag of original transaction not propagated.

### L-06: Radix Pump Address Map Lookup O(n) (Cloud)
- **Layer:** Cloud
- **File:** RadixCloudAdapter.cs (lines 244-256)
- **Problem:** Linear scan of dictionary for (PumpAddr, Fp) match instead of composite key lookup.

### L-07: Inconsistent Timestamp Handling Across Cloud Adapters (Cloud)
- **Layer:** Cloud
- **Files:** All cloud adapter timestamp parsing methods
- **Problem:** Radix/Petronite fall back to UtcNow; Advatec uses different logic; DOMS requires both. No consistent pattern.

### L-08: Incomplete AdvatecAdapter.GetPumpStatusAsync (Desktop)
- **Layer:** Desktop Edge
- **File:** AdvatecAdapter.cs (lines 293-296)
- **Problem:** Unconditionally returns empty array with no documentation explaining why.

### L-09: Radix Pre-Auth Token Overwrite Without Collision Detection (Desktop)
- **Layer:** Desktop Edge
- **File:** RadixAdapter.cs (line 536)
- **Problem:** 16-bit modulo token can repeat after 65K+ requests. New pre-auth silently overwrites old.

### L-10: No Retry Logic for Radix Mode Change Failures (Desktop)
- **Layer:** Desktop Edge
- **File:** RadixAdapter.cs (lines 250-254)
- **Problem:** EnsureModeAsync failure aborts entire fetch cycle. No retry. Upload uses Polly but fetch doesn't.

### L-11: OdooOrderId Populated Inconsistently Between Desktop Adapters
- **Layer:** Desktop Edge
- **Files:** RadixAdapter.cs (line 494), AdvatecAdapter.cs (line 554)
- **Problem:** Advatec populates OdooOrderId from pre-auth match but Radix doesn't. Inconsistent cross-adapter.

### L-12: Transaction Dedup Scope Unclear (Android)
- **Layer:** Android Edge
- **File:** TransactionBufferManager.kt (lines 45-49)
- **Problem:** Dedup is `unique(fccTransactionId + siteCode)`. Cross-adapter duplicate for same real-world event is not prevented.

### L-13: Pump Address Map Lenient Parsing (Android)
- **Layer:** Android Edge
- **File:** RadixAdapter.kt (lines 1122-1142)
- **Problem:** Silent skips on invalid pump numbers. Parse errors return empty map causing all pre-auth requests to fail silently.

### L-14: JplHeartbeatManager Dead Connection Detection Rigid (Android)
- **Layer:** Android Edge
- **File:** JplHeartbeatManager.kt (line 23)
- **Problem:** Hardcoded 3x multiplier. No exponential backoff. No jitter. False reconnects on slow networks.

### L-15: Portal Error Context Insufficient in Token Generation
- **Layer:** Portal
- **File:** bootstrap-token.component.ts (lines 447-451)
- **Problem:** Same error message for network, validation, and permission failures. No logging of actual error.

### L-16: Portal Logging Service Backend Disabled by Default
- **Layer:** Portal
- **File:** logging.service.ts (lines 26, 65)
- **Problem:** `backendLoggingEnabled = false` hardcoded. No way to enable without code change. Client errors invisible.

---

## Cross-Layer Consistency Issues

### XL-01: Currency Factor Mapping Diverges Across All Layers

| Currency | Android Edge | Desktop Edge | Cloud Radix | Cloud Advatec | Cloud Petronite |
|----------|-------------|-------------|-------------|---------------|-----------------|
| KWD/BHD/OMR | 1000 | 1000 | 100 (!) | 1000 | 1000 |
| JPY/KRW | 1 | 1 | 100 (!) | 1 (missing KRW) | 1 (missing) |
| TZS | 1 | 1 | 100 (!) | 1 | missing |
| Default | 100 | 100 | 100 | 100 | 100 |

**Radix Cloud adapter always uses 100** regardless of currency. This means KWD transactions processed through Radix Cloud have a **10x error** compared to edge agent values.

**Recommendation:** Centralize currency factor mapping into a shared utility. Apply consistently across all adapters and layers.

### XL-02: Adapter Feature Parity Gaps

| Feature | Radix | DOMS/JPL | Petronite | Advatec |
|---------|-------|----------|-----------|---------|
| Pull Mode | Yes | Yes (JPL) | No | No |
| Push Mode | Yes | No | Yes | Yes |
| Pre-Auth | Yes | No | Partial | Yes |
| Pump Status | Empty | Stub | Synthesized | Empty |
| Fiscalization | No | No | No | Desktop only |
| Heartbeat | Yes | Yes (JPL) | No | Yes (webhook) |
| Catch-Up Pull | Yes | Unclear | No | No |
| Currency Mapping | Fixed 100 (Cloud) | Not checked | Partial | Partial |

### XL-03: Desktop vs Android Implementation Divergence

| Component | Android | Desktop | Divergence |
|-----------|---------|---------|------------|
| DOMS Adapter | Stub + JPL impl | Not present | Desktop missing DOMS entirely |
| Petronite Adapter | Full impl | Not present | Desktop missing Petronite |
| Buffer States | 4 states (no terminal) | Status + SyncStatus (dual) | Different state models |
| Pre-Auth Cleanup | Passive (on heartbeat) | Passive (on normalize) | Both inadequate |
| WebSocket | Present | Present | Implementation may differ |
| Connectivity Binding | Network-aware socket | Standard | Android-specific feature |

### XL-04: Portal-to-Cloud API Contract Mismatches

| Field | Cloud API | Portal Model | Status |
|-------|-----------|-------------|--------|
| SiteUsesPreAuth | Present | **Missing** | Cannot toggle |
| CatchUpPullIntervalSeconds | Present | **Missing** | Cannot configure |
| HybridCatchUpIntervalSeconds | Present | **Missing** | Cannot configure |
| AdvatecDevicePort | Present | In model, **missing from draft** | Lost on edit |
| AdvatecWebhookListenerPort | Present | In model, **missing from draft** | Lost on edit |
| AdvatecWebhookToken | Present | In model, **missing from draft** | Lost on edit |
| AdvatecEfdSerialNumber | Present | In model, **missing from draft** | Lost on edit |
| AdvatecCustIdType | Present | In model, **missing from draft** | Lost on edit |

---

## Remediation Priority Matrix

### Immediate (This Sprint)

| # | Issue | Layer | Effort | Risk |
|---|-------|-------|--------|------|
| C-01 | Advatec multi-item receipt data loss | Cloud | 4h | High - financial data |
| C-04 | Portal Advatec config fields lost on edit | Portal | 2h | High - config loss |
| C-06 | DOMS adapter stub crash | Android | 1h | Medium - factory guard |
| H-07 | Webhook startup failure silent | Desktop | 2h | High - silent failure |
| XL-01 | Currency factor mapping divergence | All | 8h | High - financial accuracy |

### Short-Term (Next 2 Sprints)

| # | Issue | Layer | Effort | Risk |
|---|-------|-------|--------|------|
| C-02 | Advatec pump mapping missing | Cloud | 6h | Medium - new schema |
| C-03 | Petronite timestamp fallback | Cloud | 2h | Medium - data integrity |
| C-05 | Catch-up interval UI missing | Portal | 3h | Low - additive |
| H-02 | Bootstrap token TOCTOU | Cloud | 4h | Medium - DB constraint |
| H-03 | Cloud adapter resilience | Cloud | 6h | Medium - infra change |
| H-04 | Webhook token full scan | Cloud | 4h | Medium - index needed |
| H-05 | Desktop HttpClient leak | Desktop | 3h | Low - refactor |
| H-08 | Portal SiteUsesPreAuth | Portal | 2h | Low - additive |
| H-09 | Portal vendor-specific validation | Portal | 8h | Medium - complex |
| H-10 | Pre-auth memory leak | Android | 4h | Medium - background job |
| H-11 | Buffer terminal states | Android | 6h | Medium - schema change |
| GAP-1 | DEAD_LETTER state | Edge agents | 8h | Medium - lifecycle change |

### Medium-Term (Next Quarter)

| # | Issue | Layer | Effort | Risk |
|---|-------|-------|--------|------|
| H-01 | Radix signature validation | Cloud | 8h | Medium - security |
| H-06 | Push listener queue race | Desktop | 4h | Low - edge case |
| H-13 | JplTcpClient socket leak | Android | 2h | Low - try-finally |
| H-14 | Batch size zero | Android | 1h | Low - bound check |
| M-01 through M-23 | All medium issues | Various | ~60h total | Various |
| GAP-2 through GAP-7 | Lifecycle gaps | Various | ~40h total | Various |

### Long-Term (Backlog)

| # | Issue | Layer | Effort |
|---|-------|-------|--------|
| L-01 through L-16 | All low issues | Various | ~30h total |
| XL-02 | Feature parity gaps | All | Ongoing |
| XL-03 | Desktop/Android divergence | Edge agents | Ongoing |

---

## Appendix: File Reference

### Cloud Layer
- `src/cloud/FccMiddleware.Adapter.Advatec/AdvatecCloudAdapter.cs`
- `src/cloud/FccMiddleware.Adapter.Radix/RadixCloudAdapter.cs`
- `src/cloud/FccMiddleware.Adapter.Radix/RadixTransactionDto.cs`
- `src/cloud/FccMiddleware.Infrastructure/Adapters/CloudFccAdapterFactoryRegistration.cs`
- `src/cloud/FccMiddleware.Infrastructure/Repositories/SiteFccConfigProvider.cs`
- `src/cloud/FccMiddleware.Application/Registration/GenerateBootstrapTokenHandler.cs`
- `src/cloud/FccMiddleware.Domain/Entities/FccConfig.cs`
- `src/cloud/FccMiddleware.Domain/Models/Adapter/SiteFccConfig.cs`

### Android Edge Agent
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixAdapter.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixPushListener.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/radix/RadixXmlParser.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/DomsAdapter.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/jpl/JplTcpClient.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/doms/jpl/JplHeartbeatManager.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/petronite/PetroniteAdapter.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/adapter/advatec/AdvatecAdapter.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/buffer/TransactionBufferManager.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/CloudUploadWorker.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/preauth/PreAuthHandler.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/sync/PreAuthCloudForwardWorker.kt`
- `src/edge-agent/app/src/main/kotlin/com/fccmiddleware/edge/config/ConfigManager.kt`

### Desktop Edge Agent
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/FccAdapterFactory.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixAdapter.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixPushListener.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Radix/RadixXmlParser.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecAdapter.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Adapter/Advatec/AdvatecApiClient.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Ingestion/IngestionOrchestrator.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Buffer/Entities/BufferedTransaction.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Config/DesktopFccRuntimeConfiguration.cs`
- `src/desktop-edge-agent/src/FccDesktopAgent.Core/Sync/CloudUploadWorker.cs`

### Portal
- `src/portal/src/app/core/models/site.model.ts`
- `src/portal/src/app/core/models/agent.model.ts`
- `src/portal/src/app/features/site-config/site-detail.component.ts`
- `src/portal/src/app/features/site-config/fcc-config-form.component.ts`
- `src/portal/src/app/features/edge-agents/bootstrap-token.component.ts`
- `src/portal/src/app/core/services/logging.service.ts`
