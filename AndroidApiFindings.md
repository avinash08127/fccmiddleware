# Android Networking Integration Audit

> **Audit scope**: Edge Agent networking layer — Ktor/OkHttp cloud client, embedded local API server, DTO models, serialization, error handling, retry/circuit breaker, certificate pinning, timeout configuration, API contract alignment with cloud backend.
>
> **Date**: 2026-03-13

---

## Architecture Summary

| Component | Technology | File |
|-----------|-----------|------|
| Cloud HTTP Client | Ktor 3.0.3 + OkHttp engine | `CloudApiClient.kt` |
| Local REST API | Ktor CIO embedded server | `LocalApiServer.kt` |
| DTOs | `@Serializable` data classes | `CloudApiModels.kt`, `ApiModels.kt` |
| Serialization | kotlinx.serialization 1.7.3 | Ktor ContentNegotiation plugin |
| Error handling | Sealed class result types | per-endpoint in `CloudApiClient.kt` |
| Retry/backoff | Circuit breaker pattern | `CircuitBreaker.kt` |
| Auth tokens | AES-256-GCM in Android Keystore | `KeystoreDeviceTokenProvider.kt` |
| Cert pinning | OkHttp CertificatePinner | `CloudApiClient.buildKtorClient()` |
| DI | Koin 4.0.1 | `AppModule.kt` |
| Network binding | Android Network API | `BoundSocketFactory.kt` |

### Request flow

```
Screen (ProvisioningActivity / DiagnosticsActivity / SettingsActivity)
  → ViewModel (ProvisioningViewModel)
    → CloudApiClient interface
      → HttpCloudApiClient (Ktor + OkHttp)
        → Backend endpoint (AgentController / TransactionsController)
```

Workers (CloudUploadWorker, ConfigPollWorker, PreAuthCloudForwardWorker) are triggered by `CadenceController` on a periodic schedule, not by user interaction. They follow the same `CloudApiClient → backend` path.

---

## Findings

### NET-001: Telemetry `sequenceNumber` type mismatch — Long (client) vs Int (server)
- **Severity**: High
- **Location**: `CloudApiModels.kt:204` (`TelemetryPayload.sequenceNumber: Long`) vs `SubmitTelemetryRequest.cs:28` (`int SequenceNumber`)
- **Trace**: `CloudUploadWorker.reportTelemetry()` → `CloudApiClient.submitTelemetry()` → `POST /api/v1/agent/telemetry` → `AgentController.SubmitTelemetry()`
- **Description**: The Edge Agent declares `sequenceNumber` as `Long` (Kotlin — 64-bit signed integer). The cloud contract declares `SequenceNumber` as `int` (C# — 32-bit signed integer, max 2,147,483,647). The sequence number is monotonically incremented per device and never resets. A device reporting telemetry every 30 seconds would reach `Int.MaxValue` in ~2,040 years, so overflow is not a practical risk. However, the type mismatch means the serializer may produce a JSON number larger than `Int32.MaxValue` after extremely long uptimes or if the sequence counter is ever corrupted. The backend's `[Range(1, int.MaxValue)]` validation would reject such a payload with HTTP 400.
- **Impact**: Theoretical: rejected telemetry after 2+ billion reports. Practical: unlikely but represents a silent contract divergence that could surface under data corruption.
- **Fix**: Align types — either change the client to `Int` (sufficient for practical use) or change the backend to `long`.

### NET-002: Telemetry identity fields sent as `String` but backend expects `Guid`
- **Severity**: High
- **Location**: `CloudApiModels.kt:198-203` (`TelemetryPayload.deviceId: String`, `legalEntityId: String`) vs `SubmitTelemetryRequest.cs:13-17` (`Guid DeviceId`, `Guid LegalEntityId`)
- **Trace**: `TelemetryReporter.buildPayload()` → `CloudApiClient.submitTelemetry()` → `POST /api/v1/agent/telemetry`
- **Description**: The Edge Agent sends `deviceId` and `legalEntityId` as raw strings in JSON. The C# backend binds these to `Guid` properties. ASP.NET Core's `System.Text.Json` deserializer can parse a JSON string `"xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"` into a `Guid` — so this works as long as the string is a valid UUID with hyphens. If the Edge Agent ever sends a malformed or empty string (e.g., from corrupt EncryptedPrefs), the backend returns HTTP 400 with no actionable error code, and the telemetry payload is silently dropped.
- **Impact**: Works in the happy path. Fragile under data corruption — no graceful degradation on the client side since the transport error is generic.
- **Fix**: Add client-side UUID format validation before submitting telemetry. Consider making the serialization explicit with a UUID type on the Kotlin side.

### NET-003: Diagnostic log `deviceId` / `legalEntityId` type mismatch — String vs Guid
- **Severity**: Medium
- **Location**: `CloudApiModels.kt:488-490` (`DiagnosticLogUploadRequest.deviceId: String`, `legalEntityId: String`) vs `DiagnosticLogUploadRequest.cs:8,15` (`Guid DeviceId`, `Guid LegalEntityId`)
- **Trace**: `CloudUploadWorker.reportDiagnosticLogs()` → `CloudApiClient.submitDiagnosticLogs()` → `POST /api/v1/agent/diagnostic-logs`
- **Description**: Same issue as NET-002 but for diagnostic log uploads. The client sends plain strings; the backend expects `Guid`. This is a fire-and-forget path, so failures are silently discarded by design, making debugging harder.
- **Impact**: Diagnostic log uploads fail silently if identity strings are malformed.
- **Fix**: Same as NET-002 — validate UUID format before submission.

### NET-004: `BUNDLED_PINS` in `HttpCloudApiClient.kt` is empty but `AppModule.kt` provides real pins
- **Severity**: Medium
- **Location**: `CloudApiClient.kt:628` (`BUNDLED_PINS = emptyList()`) vs `AppModule.kt:110-113`
- **Trace**: `AppModule.kt` → `HttpCloudApiClient.create()` → `buildKtorClient()`
- **Description**: The `HttpCloudApiClient.BUNDLED_PINS` companion constant is declared as `emptyList()` with a TODO comment. However, `AppModule.kt` passes real bootstrap pins directly via the `certificatePins` parameter. This creates a disconnect: if any code path creates an `HttpCloudApiClient` using `HttpCloudApiClient.create()` without passing explicit pins (e.g., test code or a future factory method), the fallback to `BUNDLED_PINS` yields an empty list, leaving the connection completely unpinned. The `S-006` comment specifically documents this fallback as a security guarantee.
- **Impact**: The production path is safe (AppModule passes real pins). But the empty `BUNDLED_PINS` violates the stated security contract and creates a latent unpinning risk.
- **Fix**: Populate `BUNDLED_PINS` with the same SHA-256 hashes used in `AppModule.kt`, so the fallback path is always pinned.

### NET-005: Version check sends `agentVersion` query param but backend expects `appVersion` or `agentVersion`
- **Severity**: Low
- **Location**: `CloudApiClient.kt:281` (`parameter("agentVersion", agentVersion)`) vs `VersionCheckRequest.cs:5-6` and `AgentController.cs:503`
- **Trace**: `CadenceController` → `CloudApiClient.checkVersion()` → `GET /api/v1/agent/version-check?agentVersion=...`
- **Description**: The Edge Agent sends only the `agentVersion` query parameter. The backend accepts either `appVersion` or `agentVersion`, preferring `appVersion` if both are present. If `agentVersion` is provided but `appVersion` is null, the backend uses `agentVersion` — so this works. However, the logic at `AgentController.cs:503-506` requires at least one to be non-empty, with an explicit error message referencing `appVersion` as "required". This is a documentation/naming inconsistency, not a bug.
- **Impact**: Functional — no runtime issue. Minor naming confusion.
- **Fix**: Consider aligning on a single parameter name across client and server.

### NET-006: Registration uses `cloudBaseUrl` from QR data, bypassing instance-level cert pins
- **Severity**: Medium
- **Location**: `CloudApiClient.kt:520` (`httpClient.post("$cloudBaseUrl/api/v1/agent/register")`)
- **Trace**: `ProvisioningViewModel.register()` → `cloudApiClient.registerDevice(qrData.cloudBaseUrl, request)` → `HttpCloudApiClient.registerDevice()`
- **Description**: The `registerDevice` method takes an explicit `cloudBaseUrl` parameter (from the QR code) and uses it directly in the URL, NOT the instance's `this.cloudBaseUrl`. The HTTP client still uses the OkHttp engine that was built with certificate pins for the instance's hostname. If the QR code contains a different hostname than what the client was configured with, the CertificatePinner will reject the connection (pin mismatch). This is actually correct security behavior — it prevents redirecting registration to an attacker-controlled endpoint. However, it means registration will fail if the QR code URL legitimately differs from the pre-configured URL (e.g., a different environment).
- **Impact**: Registration fails if QR URL hostname differs from configured hostname. This is likely-by-design but could cause field support confusion.
- **Fix**: Document this behavior. Consider building a separate HTTP client for registration with pins for the QR-provided hostname.

### NET-007: `updateBaseUrl` not thread-safe — `cloudBaseUrl` and `httpClient` written non-atomically
- **Severity**: Medium
- **Location**: `CloudApiClient.kt:303-317` (`updateBaseUrl()`)
- **Trace**: `ProvisioningViewModel.handleRegistrationSuccess()` → `cloudApiClient.updateBaseUrl()`
- **Description**: `updateBaseUrl()` writes `cloudBaseUrl` on line 308, then conditionally rebuilds and writes `httpClient` on line 313. Both fields are `@Volatile`, but there is no synchronization between the two writes. A concurrent cloud API call from another coroutine could read the new `cloudBaseUrl` but the old `httpClient` (with old hostname pins), or vice versa. In practice, `updateBaseUrl` is called only during provisioning when workers are not yet active, so the race window is narrow.
- **Impact**: Theoretical race condition. Low practical risk because of the provisioning timing.
- **Fix**: Guard both writes with a `synchronized` block or `Mutex` to ensure atomic update.

### NET-008: `PreAuthForwardRequest.toForwardRequest()` omits `vehicleNumber` and `customerBusinessName`
- **Severity**: Low
- **Location**: `PreAuthCloudForwardWorker.kt:260-277` vs `CloudApiModels.kt:366-391`
- **Trace**: `PreAuthCloudForwardWorker.forwardUnsyncedPreAuths()` → `record.toForwardRequest()` → `CloudApiClient.forwardPreAuth()`
- **Description**: The `PreAuthForwardRequest` DTO has `vehicleNumber` and `customerBusinessName` fields (lines 386-389 of CloudApiModels.kt), but the `toForwardRequest()` extension function in `PreAuthCloudForwardWorker.kt` does not map `vehicleNumber` or `customerBusinessName` from the `PreAuthRecord` entity. These fields will always be `null` in forwarded requests, even if the local pre-auth record has values.
- **Impact**: Cloud pre-auth records missing vehicle number and customer business name for reconciliation matching.
- **Fix**: Map `vehicleNumber` and `customerBusinessName` from `PreAuthRecord` in `toForwardRequest()`.

### NET-009: No request/response body size limit on cloud API client for large payloads
- **Severity**: Low
- **Location**: `CloudApiClient.kt:320-355` (`uploadBatch`) and `CloudApiModels.kt:14` (`CloudUploadRequest.transactions`)
- **Trace**: `CloudUploadWorker.uploadPendingBatch()` → `doUpload()` → `CloudApiClient.uploadBatch()`
- **Description**: The upload batch can contain up to 500 transactions (per `CLOUD_MAX_BATCH_SIZE`), each including `rawPayloadJson` (raw FCC JSON — potentially several KB per record). With max batch of 500 records each carrying raw payloads, a single request could be several MB. While the backend has `[RequestSizeLimit]` annotations on some endpoints (e.g., 64KB for registration), there is no explicit size limit on the upload endpoint. The OkHttp client has no max response size configured. On the 413 PayloadTooLarge path, the worker halves the batch size — this is a reactive mitigation, not preventive.
- **Impact**: Large payloads on throttled mobile connections may cause timeouts or OOM on constrained devices.
- **Fix**: Consider computing estimated payload size before upload and proactively limiting batch size. Set OkHttp response body limit for defense-in-depth.

### NET-010: Diagnostic log upload on 401 does not attempt token refresh
- **Severity**: Low
- **Location**: `CloudUploadWorker.kt:400-413` (`reportDiagnosticLogs()`)
- **Trace**: `CloudUploadWorker.reportDiagnosticLogs()` → `CloudApiClient.submitDiagnosticLogs()` → `POST /api/v1/agent/diagnostic-logs`
- **Description**: When diagnostic log upload returns `Unauthorized` (HTTP 401), the handler logs a warning but does NOT attempt a token refresh and retry, unlike all other workers (`reportTelemetry`, `uploadPendingBatch`, `pollConfig`, etc.) which all have the 401 → refresh → retry pattern. Since diagnostic logs are fire-and-forget, the impact is limited, but the inconsistency means logs are lost unnecessarily when the JWT expires just before a diagnostic upload.
- **Impact**: Diagnostic logs silently dropped on expired JWT without retry attempt.
- **Fix**: Add the standard 401 → refresh → retry pattern to `reportDiagnosticLogs()` for consistency.

### NET-011: `isLenient = true` in cloud client JSON config accepts malformed JSON
- **Severity**: Low
- **Location**: `CloudApiClient.kt:726` (`isLenient = true`)
- **Trace**: All cloud API responses deserialized via Ktor ContentNegotiation
- **Description**: The `isLenient = true` flag on the kotlinx.serialization `Json` builder allows non-standard JSON (unquoted strings, single quotes, trailing commas, etc.). This reduces strictness and could mask malformed server responses or forward-compatibility issues. The intent appears to be tolerance for edge cases, but it weakens input validation on the client side.
- **Impact**: Malformed server responses could be silently accepted instead of flagged as errors.
- **Fix**: Consider removing `isLenient = true`. `ignoreUnknownKeys = true` already handles forward compatibility. If lenient parsing is needed for a specific endpoint, apply it selectively.

### NET-012: Local API server rate limiter uses per-second window with benign race
- **Severity**: Low
- **Location**: `LocalApiServer.kt:466-478` (`SlidingWindowCounter.tryAcquire()`)
- **Trace**: All local API requests → `RateLimitPlugin` → `SlidingWindowCounter`
- **Description**: The sliding window counter uses `AtomicLong` for the window epoch-second and `AtomicInteger` for the counter. On window transition, `compareAndSet` resets the counter, but the race between two threads both detecting a new window and resetting can result in one thread's increment being lost. The comment acknowledges this as "safe direction" (slightly under-counts). However, the non-atomic read-compare-set-reset sequence means that in the exact transition second, a burst of requests could slightly exceed the configured RPS limit.
- **Impact**: Negligible — at most one extra request per window transition. The rate limiter is defense-in-depth for the localhost API.
- **Fix**: Acceptable as-is for the use case. No change needed.

### NET-013: `retryOnConnectionFailure` not explicitly configured on OkHttp client
- **Severity**: Low
- **Location**: `CloudApiClient.kt:704-720` (`buildKtorClient` OkHttp config block)
- **Trace**: All cloud API calls via `HttpCloudApiClient`
- **Description**: OkHttp's default `retryOnConnectionFailure` is `true`, meaning the client will automatically retry failed connections (e.g., on connection reset, stale socket). This is generally desirable for idempotent requests but could cause double-submission for POST endpoints if the server received the request but the response was lost. The transaction upload endpoint has batch-level idempotency (`uploadBatchId`), and pre-auth has dedup-key idempotency, so double-sends are safe for these. Token refresh is also idempotent (same refresh token → same result).
- **Impact**: No current issue due to idempotency design. But worth documenting that OkHttp's transparent retry is active.
- **Fix**: Document the reliance on OkHttp's default retry behavior. Consider adding `retryOnConnectionFailure(false)` if any non-idempotent endpoints are added in the future.

### NET-014: `ConnectivityManager` internet probe response body not consumed
- **Severity**: Low
- **Location**: `AppModule.kt:191` (`probeHttpClient.newCall(request).execute().use { it.isSuccessful }`)
- **Trace**: `ConnectivityManager.internetProbe` lambda → OkHttp execute
- **Description**: The internet probe calls `GET /health` and checks `isSuccessful` but does not explicitly consume the response body. While the `.use { }` block closes the response (which closes the body stream), not reading the body means the connection cannot be reused by OkHttp's connection pool. For a lightweight health probe running every 30 seconds, this means a new TCP+TLS handshake per probe instead of reusing the persistent connection.
- **Impact**: Slightly higher latency and resource usage for health probes. Negligible for a 30s interval.
- **Fix**: Add `it.body?.close()` or read a few bytes inside the `use` block to enable connection reuse.

### NET-015: Cloud API `ErrorResponse` contract divergence between client and server
- **Severity**: Low
- **Location**: `CloudApiModels.kt:132-136` vs backend `ErrorResponse` (includes `TraceId`, `Timestamp`, `Retryable`)
- **Trace**: All error responses from cloud API
- **Description**: The Edge Agent's `CloudErrorResponse` has only `errorCode` and `message`. The backend's `ErrorResponse` includes additional fields: `TraceId`, `Timestamp`, and `Retryable`. Because the client uses `ignoreUnknownKeys = true`, these extra fields are silently dropped during deserialization — no runtime error. However, the `Retryable` flag is never used by the client for retry decisions, which means the client cannot distinguish retryable server errors from permanent ones.
- **Impact**: The client relies solely on HTTP status codes for retry decisions, not the backend's `Retryable` hint. This works but is less precise.
- **Fix**: Consider adding `retryable: Boolean? = null` to `CloudErrorResponse` and using it in transport error handling.

---

## Positive Observations

These areas are well-implemented and require no changes:

1. **Sealed class result types**: Every cloud API call returns a sealed class that exhaustively models all HTTP outcomes (200, 201, 204, 304, 400, 401, 403, 409, 413, 429). Callers use `when` expressions with compiler-enforced exhaustive matching. This eliminates unchecked error paths.

2. **Circuit breaker pattern (M-08)**: All three cloud workers (upload, config poll, pre-auth forward) use the same `CircuitBreaker` class with CLOSED → OPEN → HALF_OPEN states, exponential backoff (1s base, 60s cap), 20-failure threshold, 5-minute half-open probe window, and immediate reset on connectivity recovery. This prevents thundering herd on cloud outages.

3. **Token refresh with mutex serialization**: The `KeystoreDeviceTokenProvider.refreshAccessToken()` uses a coroutine `Mutex` to serialize concurrent 401 handlers across workers. The first caller performs the refresh; subsequent callers wait and then see the already-refreshed token. This eliminates duplicate refresh requests.

4. **Certificate pinning with runtime rotation**: OkHttp-level SHA-256 SPKI pinning with runtime pin delivery via SiteConfig. Pins are stored in EncryptedSharedPreferences. Fail-fast on missing/invalid hostname. The `updateBaseUrl()` method rebuilds the HTTP client when the hostname changes to re-apply pins.

5. **Constant-time API key comparison**: The local API server uses `MessageDigest.isEqual()` for LAN API key validation, preventing timing side-channel attacks.

6. **Network binding via BoundSocketFactory**: Cloud traffic is bound to mobile data networks via Android's `Network.bindSocket()`, ensuring cloud API calls route over mobile data even when WiFi is connected to the FCC LAN segment.

7. **Request correlation**: The OkHttp interceptor attaches `X-Correlation-Id` to all outbound cloud requests. The local API server extracts incoming correlation IDs and propagates them through the coroutine context via `CorrelationIdElement`.

8. **Decommission propagation**: Volatile in-memory cache + persistent SharedPreferences ensures all workers see decommission state immediately without SharedPreferences I/O on every check.

9. **Batch idempotency**: Upload batches include a `uploadBatchId` (UUID) for server-side idempotency. Pre-auth forwarding uses (odooOrderId, siteCode) as the dedup key.

10. **Cleartext traffic prohibition**: `network_security_config.xml` sets `cleartextTrafficPermitted="false"` for all hosts, enforced at the Android framework level.

11. **Defense-in-depth auth on local API**: Both a global Ktor plugin (`LanApiKeyAuthPlugin`) and per-route `routeRequiresAuth()` checks enforce authentication. Even if the plugin is bypassed, individual routes independently verify access.

12. **Adaptive batch sizing (P-002)**: Upload batch size doubles on full-batch success (up to 500) and halves on 413 PayloadTooLarge (floor: 1). Resets to config default on partial batch.
