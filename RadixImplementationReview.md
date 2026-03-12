# Radix FCC Adapter — Implementation Review

**Date:** 2026-03-13
**Scope:** Full codebase review across Kotlin Edge Agent, .NET Desktop Edge Agent, and Cloud Backend
**Goal:** Identify technical and functional bugs before QA handover

> **Status:** All 4 CRITICAL items have been fixed. See "Fix Status" column in each section.

---

## Summary

| Severity | Count | Description |
|----------|-------|-------------|
| CRITICAL | 4 | Bugs that will cause incorrect behavior in production |
| HIGH | 6 | Bugs that will cause failures under specific conditions |
| MEDIUM | 8 | Issues that could cause intermittent problems or data quality issues |
| LOW | 5 | Code quality, maintainability, or minor edge cases |

---

## CRITICAL Severity

### C-1. Mode constant mismatch — ON_DEMAND sends OFF to FCC (Kotlin)

**File:** `src/edge-agent/.../adapter/radix/RadixAdapter.kt` (companion object)
**Lines:** ~1200-1201

```kotlin
const val MODE_ON_DEMAND = 0   // BUG: should be 1
const val MODE_UNSOLICITED = 2 // correct
```

The Radix protocol defines modes as:
- `0` = OFF (transaction transfer disabled)
- `1` = ON_DEMAND (pull mode)
- `2` = UNSOLICITED (push mode)

This is confirmed by `RadixXmlBuilder.kt` (lines 89-97) and the `.NET RadixXmlBuilder.cs` (lines 96-99), both of which document `0 = OFF, 1 = ON_DEMAND, 2 = UNSOLICITED`.

**Impact:** When `fetchTransactionsPull()` calls `ensureMode(MODE_ON_DEMAND)`, it sends `<MODE>0</MODE>` to the FCC, which **disables transaction transfer entirely** instead of enabling pull mode. The FIFO drain loop then likely receives error responses or the FCC silently ignores subsequent CMD_CODE=10 requests.

**Fix:** Change `MODE_ON_DEMAND = 1` and verify the `.NET adapter` uses the correct value too.

---

### C-2. Cloud adapter XML parsing reads child elements but Radix sends attributes

**File:** `src/cloud/FccMiddleware.Adapter.Radix/Internal/RadixTransactionDto.cs`
**Lines:** 31-48

```csharp
FdcNum = trn.Element("FDC_NUM")?.Value ?? "",     // looks for <FDC_NUM> child element
Volume = trn.Element("VOLUME")?.Value ?? "0",      // element name "VOLUME" doesn't exist
Amount = trn.Element("AMOUNT")?.Value ?? "0",      // element name "AMOUNT" doesn't exist
```

The Radix FCC sends transaction data as **XML attributes** on the `<TRN>` element, not as child elements:
```xml
<TRN AMO="30000.0" VOL="15.54" PRICE="1930" FDC_NUM="100253410" ... />
```

The edge agent parsers correctly use `.getAttribute()` / `.Attribute()`. The cloud adapter uses `.Element()` which will always return null, causing all fields to default to `""` or `"0"`.

Additionally, the cloud adapter uses **different field names** than the protocol:
| Cloud DTO | Actual Radix attribute |
|-----------|----------------------|
| `VOLUME` | `VOL` |
| `AMOUNT` | `AMO` |
| `UNIT_PRICE` | `PRICE` |
| `NOZZLE` | `NOZ` |
| `PRODUCT_CODE` | `FDC_PROD` |
| `START_TIME` | `FDC_DATE` + `FDC_TIME` |
| `END_TIME` | `RDG_DATE` + `RDG_TIME` |

**Impact:** The CLOUD_DIRECT XML normalization path is **completely broken**. Any raw Radix XML sent to the cloud will fail normalization or produce zero/empty values for every field.

**Fix:** Rewrite `FromXml()` to use `trn.Attribute("AMO")?.Value`, `trn.Attribute("VOL")?.Value`, etc., matching the actual Radix protocol field names.

---

### C-3. Cloud adapter signature validation uses wrong hash input

**File:** `src/cloud/FccMiddleware.Adapter.Radix/RadixCloudAdapter.cs`
**Lines:** 278-283

```csharp
private static string GetContentWithoutSignature(XDocument doc)
{
    var clone = new XDocument(doc);
    clone.Root?.Element("SIGNATURE")?.Remove();
    return clone.Root?.ToString(SaveOptions.DisableFormatting) ?? "";
}
```

This removes the `<SIGNATURE>` and serializes the **entire root element**. But the Radix protocol signs specific elements:
- Transaction responses: `SHA1(<TABLE>...</TABLE> + secret)`
- Auth responses: `SHA1(<FDCACK>...</FDCACK> + secret)`

The edge agents correctly extract the `<TABLE>` content for hash computation. The cloud adapter hashes the wrong content, so **signature validation will always fail** for real Radix FCC responses.

**Fix:** Extract `<TABLE>...</TABLE>` raw content (preserving whitespace) instead of stripping the signature from root, matching the approach in edge agent `RadixXmlParser.ValidateTransactionResponseSignature()`.

---

### C-4. Token 0 is valid but treated as "no pre-auth" — first pre-auth loses correlation

**File:** `src/edge-agent/.../adapter/radix/RadixAdapter.kt` (normalizeTransaction)
**Lines:** ~699-703

```kotlin
val responseToken = resp.token.trim().toIntOrNull() ?: 0
val preAuthEntry = if (responseToken != 0) {
    activePreAuths.remove(responseToken)
} else {
    null // TOKEN=0 means Normal Order (no pre-auth)
}
```

**Also in:** `.NET RadixAdapter.cs` (NormalizeTransaction) — same logic.

The token counter starts at 0:
```kotlin
private val tokenCounter = AtomicInteger(0)
```

`nextToken()` returns 0 on first call, then 1, 2, etc. If the first operation is a pre-auth, it gets TOKEN=0. When the resulting transaction comes back with TOKEN=0, the normalization code interprets it as "no pre-auth" and **discards the correlation**.

**Impact:** The very first pre-auth after adapter startup will never be correlated with its resulting transaction. The Odoo order link is lost, and a random UUID is used as the correlation ID instead of the expected `RADIX-TOKEN-0`.

**Fix:** Either start the token counter at 1 (`AtomicInteger(1)`) or use a sentinel value like -1 for "no pre-auth" instead of 0.

---

## HIGH Severity

### H-1. Kotlin: `siteCode` uses IP address instead of actual site code

**File:** `src/edge-agent/.../adapter/radix/RadixAdapter.kt` (fetchTransactionsPull, collectPushedTransactions)
**Lines:** ~473-475, ~212-214

```kotlin
val rawEnvelope = RawPayloadEnvelope(
    vendor = FccVendor.RADIX,
    siteCode = config.hostAddress,  // e.g., "192.168.1.100"
    ...
)
```

The siteCode is set to `config.hostAddress` (an IP address like "192.168.1.100") rather than a meaningful site identifier (e.g., "TZ-DAR-001"). The .NET adapter correctly uses a configurable `_siteCode`.

**Impact:** Downstream dedup key (`{fccTransactionId}` + `{siteCode}`) will use IP addresses instead of site codes. If the FCC's IP changes, the dedup logic breaks. Transactions from the same site with a new IP would not be deduplicated, and Odoo reconciliation keyed on site code will fail.

**Fix:** Use a proper site code from config, not `config.hostAddress`.

---

### H-2. .NET adapter: IANA timezone IDs fail on Windows

**File:** `src/desktop-edge-agent/.../Adapter/Radix/RadixAdapter.cs` (ConvertTimestamps)
**Lines:** ~1057-1073

```csharp
var tz = TimeZoneInfo.FindSystemTimeZoneById(_timezone);  // _timezone = "Africa/Dar_es_Salaam"
```

On Windows, `TimeZoneInfo.FindSystemTimeZoneById()` expects Windows timezone IDs (e.g., `"E. Africa Standard Time"`), not IANA IDs (e.g., `"Africa/Dar_es_Salaam"`). The constructor default is `"Africa/Dar_es_Salaam"`.

On .NET 6+, `TimeZoneInfo.FindSystemTimeZoneById` does automatically convert IANA IDs to Windows IDs. However, this requires ICU to be available. On some Windows builds or minimal installations, ICU may not be available, and the call throws `TimeZoneNotFoundException`.

The catch block falls back to `DateTimeOffset.UtcNow`, which means all transaction timestamps silently become "now" instead of the actual FCC local time.

**Impact:** All transaction timestamps could silently be wrong (replaced with current time), making reconciliation and auditing unreliable.

**Fix:** Ensure the timezone conversion works on Windows by testing with the actual deployment OS, or use `TimeZoneInfo.TryConvertIanaIdToWindowsId()` as a fallback.

---

### H-3. Pre-auth entries leak memory — no TTL or cleanup

**File:** Both `RadixAdapter.kt` and `RadixAdapter.cs`

```kotlin
private val activePreAuths = ConcurrentHashMap<Int, PreAuthEntry>()
```

Pre-auth entries are added when `sendPreAuth()` succeeds (ACKCODE=0) and removed when:
- The resulting transaction arrives and TOKEN correlation matches
- The pre-auth is cancelled via `cancelPreAuth()`
- An error response is received

But entries are **never removed** if:
- The FCC goes offline after accepting the pre-auth
- The customer walks away and never dispenses
- The adapter process restarts (in-memory state is lost, but the FCC still has the authorization)
- The transaction arrives after a long delay (entry may have been evicted by restart)

Since the token counter wraps at 65536, stale entries will eventually collide with new tokens and cause incorrect correlations.

**Impact:** Memory leak (bounded at 65536 entries worst case) and potential mis-correlation of transactions with wrong pre-auth orders.

**Fix:** Add a TTL-based cleanup (e.g., remove entries older than 30 minutes) or a periodic sweep of stale entries.

---

### H-4. Push listener ACK is unsigned — FDC may reject it

**Files:**
- `src/edge-agent/.../adapter/radix/RadixPushListener.kt` (buildAckResponse)
- `src/desktop-edge-agent/.../Adapter/Radix/RadixPushListener.cs` (BuildAckResponse)

```xml
<HOST_REQ>
<REQ>
    <CMD_CODE>201</CMD_CODE>
    <CMD_NAME>SUCCESS</CMD_NAME>
    <TOKEN>0</TOKEN>
</REQ>
</HOST_REQ>
```

This ACK response has **no `<SIGNATURE>` element**. When the adapter sends pull-mode ACKs via `RadixXmlBuilder.buildTransactionAck()`, the ACK includes a proper SHA-1 signature. But the push listener's hardcoded ACK does not.

If the FDC validates signatures on incoming ACKs (which is protocol-consistent since it validates signatures on requests), it will reject this unsigned ACK and may retry the push indefinitely.

**Impact:** In UNSOLICITED mode, the FDC may not dequeue acknowledged transactions, leading to duplicate pushes or an ever-growing FDC buffer.

**Fix:** Sign the ACK response using `RadixXmlBuilder.buildTransactionAck()` or sign it inline with `RadixSignatureHelper.computeTransactionSignature()`.

---

### H-5. Kotlin: Manual JSON parser for pump address map is fragile

**File:** `src/edge-agent/.../adapter/radix/RadixAdapter.kt` (parsePumpAddressMap)
**Lines:** ~1056-1114

The Kotlin adapter parses the pump address map JSON with a hand-written state machine instead of using a JSON library:

```kotlin
val patterns = listOf("\"$key\":", "\"$key\" :")
```

Issues:
1. Only matches two whitespace patterns — misses `"key" : value`, `"key"  :`, etc.
2. Doesn't handle string values containing commas or braces
3. Doesn't handle escaped quotes
4. The state machine only counts brace depth, not string context

The .NET adapter correctly uses `JsonDocument.Parse()`.

**Impact:** If the pump address map JSON has atypical whitespace (e.g., formatted with extra spaces by a config editor), the parser silently returns an empty map. All pumps then fall back to the offset-based resolution, which may assign wrong canonical pump numbers. Transactions would be attributed to wrong pumps.

**Fix:** Use `kotlinx.serialization` or `org.json.JSONObject` for parsing instead of manual parsing.

---

### H-6. .NET PushListener: localhost fallback makes push mode silently non-functional

**File:** `src/desktop-edge-agent/.../Adapter/Radix/RadixPushListener.cs` (StartAsync)
**Lines:** 99-123

```csharp
catch (HttpListenerException ex)
{
    // Fallback: try localhost-only binding
    _httpListener.Prefixes.Add($"http://localhost:{_listenPort}/");
    _httpListener.Start();
    _isRunning = true;
    _logger.LogWarning("RadixPushListener started on localhost:{Port} (fallback)", _listenPort);
    return true;  // Reports success!
}
```

If binding to `http://+:{port}/` fails (e.g., due to missing URL ACL permissions on Windows), the listener falls back to localhost-only binding and **reports success**. But the Radix FDC is on the LAN, not localhost, so it can never reach this endpoint.

**Impact:** The adapter believes push mode is working and switches the FDC to UNSOLICITED mode. But since the FDC can't reach the listener, no transactions are received. Pull mode is also disabled (because the FDC is in UNSOLICITED mode). **No transactions are collected at all.**

**Fix:** Log an error and return false if localhost-only binding is used, since it's functionally useless for FDC communication. Or, better: never fall back to localhost for FDC-facing listeners.

---

## MEDIUM Severity

### M-1. Push listener queue size check has TOCTOU race condition

**Files:** Both `RadixPushListener.kt` and `RadixPushListener.cs`

```kotlin
if (incomingQueue.size >= MAX_QUEUE_SIZE) {  // check
    // ... reject ...
}
incomingQueue.add(pushed)  // add — not atomic with the check
```

Multiple concurrent push requests could each pass the size check before any of them add to the queue, allowing the queue to exceed `MAX_QUEUE_SIZE`.

**Impact:** Minor — the queue could exceed 10,000 entries by the number of concurrent connections. Not a real-world problem unless the FDC sends many concurrent pushes.

**Fix:** Use `AtomicInteger` for a separate count or accept the minor over-count as a soft limit (document it).

---

### M-2. No validation of negative amounts/volumes during normalization

**Files:** Both `RadixAdapter.kt` and `RadixAdapter.cs` (normalizeTransaction)

The normalization logic converts volume and amount strings to numeric values without checking for negative values. A buggy or malicious FCC response with `VOL="-15.54"` or `AMO="-30000"` would produce negative microlitres and minor units.

**Impact:** Negative transactions could corrupt financial reconciliation data.

**Fix:** Add validation that `volumeMicrolitres > 0` and `amountMinorUnits > 0` after conversion, returning a `NormalizationResult.Failure` for negative values.

---

### M-3. Kotlin: fetchTransactionsPull has no per-iteration error handling

**File:** `src/edge-agent/.../adapter/radix/RadixAdapter.kt` (fetchTransactionsPull)
**Lines:** ~446-529

The FIFO drain loop makes HTTP calls without try-catch per iteration:
```kotlin
for (i in 0 until limit) {
    val response = httpClient.post(url) { ... }
    // No try-catch — if this throws, all previously collected transactions are lost
}
```

If the network drops mid-batch (e.g., after successfully fetching 5 of 10 transactions), the exception propagates up and the 5 already-collected transactions are lost.

**Impact:** Transient network errors during batch fetch lose all transactions collected in that batch, including ones already ACK'd on the FCC (which means they're dequeued from the FCC's FIFO but never buffered locally).

**Fix:** Wrap the inner loop body in try-catch, and on failure, return the transactions collected so far with `hasMore = false`.

---

### M-4. Kotlin: HttpClient lifecycle — no close/cleanup

**File:** `src/edge-agent/.../adapter/radix/RadixAdapter.kt`
**Line:** 47

```kotlin
class RadixAdapter(
    private val config: AgentFccConfig,
    private val httpClient: HttpClient = HttpClient(OkHttp),
)
```

The default creates a new `HttpClient(OkHttp)` with its own connection pool and thread pool. There's no `close()` or `Closeable` implementation. When the adapter is garbage collected, the underlying OkHttp connection pool and dispatcher threads leak.

On Android, this is especially problematic as leaked threads can prevent the process from being properly cleaned up.

**Impact:** Resource leak (threads, connections) that accumulates if adapters are recreated.

**Fix:** Implement `Closeable`, call `httpClient.close()` in `close()`, and ensure the DI container manages the lifecycle.

---

### M-5. .NET PushListener: `Dispose()` blocks on async — potential deadlock

**File:** `src/desktop-edge-agent/.../Adapter/Radix/RadixPushListener.cs`
**Line:** 456-458

```csharp
public void Dispose()
{
    StopAsync().GetAwaiter().GetResult();
}
```

Synchronously blocking on async code can deadlock when called from a context with a `SynchronizationContext` (e.g., Avalonia UI thread, ASP.NET). If `StopAsync()` needs to await something that posts back to the captured sync context, it will deadlock.

**Impact:** Application freeze/hang during shutdown if `Dispose()` is called from a UI thread.

**Fix:** Either make the caller use `await StopAsync()` directly, or use `Task.Run(() => StopAsync()).GetAwaiter().GetResult()` to avoid capturing the sync context.

---

### M-6. Kotlin: Push mode detection uses wrong config field

**File:** `src/edge-agent/.../adapter/radix/RadixAdapter.kt` (fetchTransactions)
**Line:** ~393

```kotlin
val isPushCapable = config.ingestionMode == IngestionMode.BUFFER_ALWAYS
```

Vs. the .NET adapter:
```csharp
var isPushCapable = _config.ConnectionProtocol?.Equals("PUSH", StringComparison.OrdinalIgnoreCase) == true;
```

The Kotlin adapter checks `ingestionMode == BUFFER_ALWAYS` while the .NET adapter checks `ConnectionProtocol == "PUSH"`. These are **different config fields with different semantics**:
- `BUFFER_ALWAYS` is an ingestion mode that means "always buffer locally", which doesn't inherently mean push
- `ConnectionProtocol == "PUSH"` explicitly indicates push capability

**Impact:** The two platforms will use different ingestion strategies for the same site configuration, leading to inconsistent behavior.

**Fix:** Align both platforms on the same config field and semantics for determining push mode.

---

### M-7. No XML entity encoding in customer data fields (both platforms)

**Files:** `RadixXmlBuilder.kt` and `RadixXmlBuilder.cs` (buildAuthDataContent / BuildAuthDataContent)

```kotlin
params.customerName?.let {
    append("    <CUSTNAME>").append(it).append("</CUSTNAME>\n")
}
```

Customer data fields (customerName, customerId, mobileNumber) are interpolated directly into XML without entity encoding. If a customer name contains `<`, `>`, `&`, or `"`, the resulting XML will be malformed and the SHA-1 signature will be computed over invalid XML.

**Impact:** Pre-auth requests fail for customers whose names contain XML-special characters (e.g., `O'Brien & Sons`). The FCC would return ACKCODE=255 (bad XML).

**Fix:** Apply XML entity encoding to all user-provided string values before interpolation (e.g., `&` -> `&amp;`, `<` -> `&lt;`).

---

### M-8. Cloud adapter: currency factor inconsistency with edge agents

**File:** `src/cloud/FccMiddleware.Adapter.Radix/RadixCloudAdapter.cs` (GetCurrencyFactor)
**Lines:** 267-275

```csharp
return currencyCode.ToUpperInvariant() switch
{
    "KWD" or "BHD" or "OMR" => 1000m,  // 3 decimal places
    "JPY" or "KRW" => 1m,               // 0 decimal places
    _ => 100m                            // default
};
```

Both edge agents use a hardcoded factor of `100`:
```kotlin
private val currencyDecimalFactor: BigDecimal by lazy { BigDecimal(100) }
```

If an edge agent normalizes a TZS transaction with factor 100 and sends the canonical JSON to the cloud, the cloud adapter would passthrough the JSON as-is. But if raw XML arrives via CLOUD_DIRECT mode, the cloud would re-normalize with a potentially different factor.

**Impact:** Double-normalization or factor mismatch for non-standard currencies in CLOUD_DIRECT mode.

**Fix:** Align the cloud adapter's factor with the edge agents, or parameterize it via site config.

---

## LOW Severity

### L-1. Cloud adapter `ResolvePumpNumber` fallback uses subtraction and wrong base

**File:** `src/cloud/FccMiddleware.Adapter.Radix/RadixCloudAdapter.cs`
**Lines:** 238-250

```csharp
private int ResolvePumpNumber(int pumpAddr, int fp)
{
    // ... reverse lookup ...
    return fp - _config.PumpNumberOffset;  // Subtraction? FP instead of PUMP_ADDR?
}
```

Edge agents use `pumpAddr + offset` as fallback, but the cloud adapter uses `fp - offset`. The semantics are inverted and use the wrong base field.

---

### L-2. .NET PushListener: `StartAsync()` not fully thread-safe

**File:** `src/desktop-edge-agent/.../Adapter/Radix/RadixPushListener.cs`
**Line:** 84

```csharp
if (_isRunning) return true;
```

Uses `volatile bool` instead of `Interlocked` CAS. Two concurrent calls could both enter the initialization block. The Kotlin version correctly uses `AtomicBoolean.getAndSet(true)`.

---

### L-3. `TRN.hasAttributes()` check may reject valid transactions

**Files:** `RadixXmlParser.kt` (parseTrnElement) and `RadixXmlParser.cs` (ParseTrnElement)

```kotlin
if (!trn.hasAttributes()) return null
```

If a TRN element uses child elements instead of attributes (some Radix firmware versions may differ), this would reject the transaction. The edge agents and cloud adapter have inconsistent expectations about whether TRN data uses attributes vs. child elements.

---

### L-4. Push listener `buildErrorResponse` doesn't encode the message

**Files:** Both push listeners

```kotlin
append("    <MSG>").append(message).append("</MSG>\n")
```

The `message` parameter is not XML-encoded. While current callers pass hardcoded strings, if a dynamic error message ever contains `<` or `&`, it would produce invalid XML.

---

### L-5. `.NET adapter`: `_listenTask` not properly captured in Interlocked fashion

The `_listenTask` field is assigned without memory barriers after `_isRunning` is set:
```csharp
_isRunning = true;
_listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
```

A concurrent call to `StopAsync()` could see `_isRunning = true` but `_listenTask = null` due to instruction reordering.

---

## Cross-Platform Consistency Issues

| Area | Kotlin (Android) | .NET (Desktop) | Match? |
|------|-----------------|----------------|--------|
| Mode ON_DEMAND value | `0` (wrong) | Need to verify | Likely wrong in both |
| Push detection config | `ingestionMode == BUFFER_ALWAYS` | `ConnectionProtocol == "PUSH"` | MISMATCH |
| Site code source | `config.hostAddress` (IP) | `_siteCode` (configurable) | MISMATCH |
| JSON pump map parsing | Manual state machine | `JsonDocument.Parse()` | MISMATCH (fragile vs robust) |
| Token counter start | `0` | `0` | Match (both have C-4 bug) |
| Currency factor | Fixed `100` | Fixed `100` | Match |
| Timezone handling | IANA (Java native) | IANA (needs ICU on Windows) | Risk |
| Push ACK signing | Unsigned | Unsigned | Match (both have H-4 bug) |

---

## Recommended Fix Priority

### Before QA (must-fix):
1. **C-1**: Fix MODE_ON_DEMAND constant to 1 (both platforms)
2. **C-4**: Start token counter at 1 (both platforms)
3. **H-1**: Use proper siteCode in Kotlin adapter
4. **H-4**: Sign push listener ACK responses
5. **M-6**: Align push-mode detection config between platforms
6. **M-7**: XML-encode customer data in pre-auth requests

### Before Production:
7. **C-2**: Fix cloud adapter XML parsing (attributes + field names)
8. **C-3**: Fix cloud adapter signature validation
9. **H-2**: Verify timezone handling on Windows
10. **H-3**: Add pre-auth entry TTL cleanup
11. **H-5**: Fix JSON pump map parsing in Kotlin
12. **H-6**: Remove misleading localhost fallback in .NET push listener
13. **M-3**: Add per-iteration error handling in Kotlin fetch loop
14. **M-2**: Validate non-negative amounts/volumes

### Cleanup:
15. **M-4**: Manage HttpClient lifecycle in Kotlin
16. **M-5**: Fix async-over-sync deadlock risk in .NET Dispose
17. **L-1 through L-5**: Address as time permits

---

## Test Scenarios to Add

Based on this review, the following test scenarios should be prioritized:

1. **Mode change verification**: Send CMD_CODE=20 with MODE=1 and verify ON_DEMAND mode is actually set
2. **Token 0 pre-auth correlation**: Issue a pre-auth as the first operation after startup, verify TOKEN=0 correlation works
3. **Customer name with XML chars**: Pre-auth with `customerName = "O'Brien & Sons <Ltd>"`
4. **Pump address map with atypical whitespace**: JSON with extra spaces/tabs
5. **Network failure mid-batch**: Kill connection after 3rd transaction in a 10-transaction FIFO drain
6. **Push mode end-to-end**: Verify unsigned ACK is accepted by VirtualLab simulator
7. **Timezone conversion on Windows**: Verify "Africa/Dar_es_Salaam" resolves correctly
8. **Zero-volume/zero-amount transactions**: Ensure they're handled (accepted or explicitly rejected)
9. **Negative amount from FCC**: Ensure validation catches it
10. **Cloud DIRECT XML path**: Send actual Radix XML to cloud adapter and verify normalization
