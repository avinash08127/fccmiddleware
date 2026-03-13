# Security Findings

## Module: FleetManagement (Edge Agent Registration, Telemetry, Monitoring)

---

### FM-S01: Bootstrap token printed in QR code window — XSS via `document.write` with unescaped values
- **Severity**: High
- **Location**: `bootstrap-token.component.ts:578-593`
- **Trace**: Portal → `printQr()` → `window.open()` → `document.write()`
- **Description**: The `printQr()` method injects `token.siteCode` and `token.tokenId` directly into an HTML string via template literals, then writes it to a new window via `document.write()`. If the `siteCode` or `tokenId` contains HTML/script content (e.g., a crafted siteCode like `<img onerror=alert(1) src=x>`), it would execute in the new window's context. While siteCode and tokenId are typically server-generated, they are user-controllable inputs (admin creates sites with custom codes).
- **Impact**: Stored XSS if a malicious site code is created and an admin generates a bootstrap token for it.
- **Fix**: HTML-encode all interpolated values before `document.write()`, or use DOM APIs to create elements.

### FM-S02: Registration endpoint is AllowAnonymous — rate limiting is the only protection
- **Severity**: Medium
- **Location**: `AgentController.cs:166-167`
- **Trace**: External → `POST /api/v1/agent/register` → `[AllowAnonymous]`
- **Description**: The registration endpoint is publicly accessible (AllowAnonymous) and authenticated only by the bootstrap token in the body/header. While rate limiting (`"registration"` policy) is applied, a sustained brute-force attack against bootstrap tokens is possible. Bootstrap tokens are 32-byte random values (256-bit entropy), making brute force computationally infeasible, but the endpoint still accepts unauthenticated traffic without IP-based blocking beyond the rate limiter.
- **Impact**: Low practical risk due to token entropy, but the endpoint is a potential target for enumeration or resource exhaustion attacks.
- **Recommendation**: Consider adding IP-based blocking after N failed attempts, or requiring a lightweight proof-of-work.

### FM-S03: Token refresh endpoint is AllowAnonymous — no device JWT required
- **Severity**: Medium
- **Location**: `AgentController.cs:272-273`
- **Trace**: External → `POST /api/v1/agent/token/refresh` → `[AllowAnonymous]`
- **Description**: The token refresh endpoint accepts only the opaque refresh token in the request body — no device JWT is required. This means a leaked refresh token alone is sufficient to obtain a new valid device JWT. The refresh token reuse detection (FM-T: line 43 in RefreshDeviceTokenHandler) mitigates the impact by revoking all tokens if a used refresh token is replayed, but the initial theft of a single refresh token grants immediate access.
- **Impact**: A stolen refresh token alone grants full device-level API access until the next legitimate refresh triggers reuse detection.
- **Recommendation**: Consider requiring the current (even expired) device JWT alongside the refresh token to bind the refresh operation to the original device identity.

### FM-S04: Decommission endpoint does not confirm action or require re-authentication
- **Severity**: Low
- **Location**: `AgentController.cs:312-360`
- **Trace**: Portal → `POST /api/v1/admin/agent/{deviceId}/decommission`
- **Description**: Decommissioning a device is an irreversible destructive action (revokes all tokens, deactivates registration). The endpoint requires `PortalAdminWrite` policy but does not require step-up authentication, confirmation, or a reason/comment. Any admin can decommission any device with a single POST.
- **Impact**: Accidental or malicious decommission with no additional safeguard. Combined with FM-F05 (no `decommissionedBy` audit), tracing responsibility is harder.

### FM-S05: Diagnostic log entries are rendered as raw text with `<pre>` tag — potential stored XSS
- **Severity**: Medium
- **Location**: `agent-detail.component.ts:432`
- **Trace**: Portal → `diagnosticLogs()` → `batch.logEntries.join('\n')` → `<pre>` element
- **Description**: Diagnostic log entries from edge devices are rendered inside a `<pre>` tag using Angular's text interpolation (`{{ batch.logEntries.join('\n') }}`). Angular's default interpolation (`{{ }}`) HTML-escapes values, so this is **safe** in the default configuration. However, if this were ever changed to `[innerHTML]`, it would become an XSS vector since log entries come from untrusted edge devices.
- **Impact**: Currently safe due to Angular's built-in escaping. Flagged as a defense-in-depth concern.

### FM-S06: QR code contains the raw bootstrap token and cloud API URL in plaintext
- **Severity**: Medium
- **Location**: `bootstrap-token.component.ts:535-544`
- **Trace**: Portal → `generateQrCode()` → JSON payload with `pt` (provisioning token) and `cu` (cloud URL)
- **Description**: The QR code payload contains the raw provisioning token and the cloud API base URL in a JSON object. Anyone who can photograph or scan the QR code (e.g., from a screen, printout, or screenshot) can extract the token and use it to register a rogue device.
- **Impact**: Physical security risk. A printed QR code left unattended or a screenshot shared via chat/email could be used to register an unauthorized device.
- **Recommendation**: Document that QR codes should be treated as sensitive credentials. Consider adding a time-limited additional verification step.

### FM-S07: `PortalAccessResolver` returns `IsValid=true` with `AllowAllLegalEntities=true` for SystemAdmin even with no legal_entities claim
- **Severity**: Low
- **Location**: `PortalAccessResolver.cs:18-21`
- **Trace**: All portal endpoints → `_accessResolver.Resolve(User)` → `CanAccess()`
- **Description**: If a user has the `SystemAdmin` role and either has no `legal_entities` claims or has a wildcard `*`, they get `AllowAllLegalEntities=true`. This is by design for super-admins, but it means a compromised SystemAdmin account can access all tenants' fleet data without restriction.
- **Impact**: Blast radius of a compromised admin account is all tenants. No tenant-level scoping for admins.

---

## Module: Transactions

---

### TX-S01: Radix CLOUD_DIRECT endpoint is AllowAnonymous — USN codes are small integers enabling enumeration
- **Severity**: High
- **Location**: `TransactionsController.cs:136` (`[AllowAnonymous]`), `TransactionsController.cs:146-148` (USN code header)
- **Trace**: External → `POST /api/v1/ingest/radix` → `[AllowAnonymous]` → USN-Code header lookup
- **Description**: The Radix XML ingest endpoint is publicly accessible (`AllowAnonymous`). Authentication relies on the `X-Usn-Code` header (an integer from 1-999999) mapped to a site configuration, followed by SHA-1 signature validation against the site's shared secret. While rate limiting (`anonymous-ingress` policy) is applied and the SharedSecret check gates actual ingestion, the USN code lookup itself succeeds or fails before signature validation. An attacker can enumerate all active USN codes by observing response differences: `USN_NOT_FOUND` (404) vs `SITE_NOT_CONFIGURED` (401) vs signature validation (200/400). The 404 vs 401 distinction reveals which USN codes have active sites and which have missing SharedSecrets.
- **Impact**: USN code enumeration reveals active site deployments. Combined with knowledge of the Radix protocol, this could enable targeted attacks against specific sites.
- **Fix**: Return the same error response for both USN-not-found and missing-secret cases. Consider requiring a static pre-shared API key header before USN lookup.

### TX-S02: Radix signature validation uses SHA-1 — cryptographically weak hash
- **Severity**: Medium
- **Location**: `RadixCloudAdapter.cs` (signature validation), `RadixSignatureHelper.cs`
- **Trace**: Radix XML push → signature extraction → SHA1(TABLE + SharedSecret) comparison
- **Description**: The Radix signature verification uses SHA-1 to hash the TABLE element concatenated with the shared secret. SHA-1 has known collision vulnerabilities (SHAttered, 2017). While this is a keyed construction (not a plain hash), the use of SHA-1 in any security-critical context is increasingly discouraged. The Radix protocol specification likely mandates SHA-1, limiting options.
- **Impact**: Theoretical forgery risk. An attacker with significant computational resources could craft a TABLE element with the same SHA-1 hash as a legitimate one. Practical risk is low given the keyed construction and 30-byte+ shared secrets.
- **Recommendation**: Document the SHA-1 dependency as a known protocol limitation. If the Radix protocol supports SHA-256, migrate.

### TX-S03: OpsTransactionsController.GetTransactionById leaks transaction existence across tenants
- **Severity**: Medium
- **Location**: `OpsTransactionsController.cs:255-266`
- **Trace**: Portal → `GET /api/v1/ops/transactions/{id}` → fetch → access check → 403 vs 404
- **Description**: The controller first fetches the transaction by ID (bypassing query filters via `IgnoreQueryFilters()`), then checks if the authenticated user can access the transaction's legal entity. If the transaction exists but belongs to another tenant, the response is `Forbid()` (403). If the transaction doesn't exist at all, the response is `NotFound()` (404). This response difference allows an authenticated portal user to probe whether a transaction GUID exists in any tenant by observing 403 vs 404.
- **Impact**: Cross-tenant transaction existence oracle. While transaction IDs are random GUIDs (hard to guess), a user who obtains a GUID from another source (e.g., logs, shared reports) can confirm its existence.
- **Fix**: Return 404 for both not-found and forbidden cases: `if (transaction is null || !access.CanAccess(transaction.LegalEntityId)) return NotFound(...)`.

### TX-S04: S3 raw payload archiver uses unsanitized fccTransactionId in object key
- **Severity**: Medium
- **Location**: `S3RawPayloadArchiver.cs:56`
- **Trace**: Ingestion → `ArchiveAsync()` → S3 key construction with `fccTransactionId`
- **Description**: The S3 object key is constructed as `raw-payloads/{legalEntityId}/{siteCode}/{year}/{month}/{fccTransactionId}.json`. The `fccTransactionId` comes from vendor-specific normalization and could contain path-like characters (`/`, `..`, `\0`). While the AWS S3 SDK URL-encodes keys and S3 treats keys as opaque strings (no directory hierarchy), a crafted `fccTransactionId` like `../../other-tenant/data` would create an object at `raw-payloads/{lei}/{site}/{year}/../../other-tenant/data.json` which S3 normalizes to `raw-payloads/{lei}/other-tenant/data.json`, potentially colliding with or overwriting another tenant's archive.
- **Impact**: Cross-tenant archive collision or overwrite if a vendor adapter produces `fccTransactionId` values containing path traversal sequences.
- **Fix**: Sanitize `fccTransactionId` by replacing `/`, `\`, `..`, and null bytes with safe characters, or use a UUID-based key instead.

### TX-S05: Petronite and Advatec webhook endpoints return error details in 200 responses
- **Severity**: Low
- **Location**: `TransactionsController.cs:314` (Petronite), `TransactionsController.cs:403` (Advatec)
- **Trace**: Webhook push → validation failure → `200 OK` with `{ status: "REJECTED", errorCode: "..." }`
- **Description**: When ingestion fails for webhook payloads, the endpoints return HTTP 200 with the internal error code (e.g., `VALIDATION.MISSING_REQUIRED_FIELD`, `ADAPTER_NOT_REGISTERED`, `NORMALIZATION_ERROR`). These error codes reveal internal processing details to external webhook callers. Returning 200 is intentional (to prevent vendor retries), but the error code content leaks implementation details.
- **Impact**: Information disclosure to external vendors — internal error codes reveal adapter structure and validation pipeline internals.
- **Fix**: Return a generic error code (e.g., `PROCESSING_ERROR`) to external callers while logging the detailed code internally.

### TX-S06: ILike search patterns in OpsTransactionsController are not escaped for LIKE wildcards
- **Severity**: Low
- **Location**: `OpsTransactionsController.cs:124, 129`
- **Trace**: Portal → `GET /api/v1/ops/transactions?fccTransactionId=%25` → `ILike(item.FccTransactionId, "%")`
- **Description**: The `fccTransactionId` and `odooOrderId` filters use `EF.Functions.ILike()` with a prefix pattern (`$"{value.Trim()}%"`). While the values are parameterized (no SQL injection), the LIKE pattern wildcards `%` and `_` within user input are not escaped. A user can pass `%` as the search term to match all transactions, or `_____` to match any 5-character ID.
- **Impact**: No security breach, but the search behavior may surprise users. A search for literal `%` or `_` characters in transaction IDs would not work as expected.
- **Fix**: Escape `%` and `_` in the user input before building the LIKE pattern (e.g., `value.Replace("%", "\\%").Replace("_", "\\_")`).

---

## Module: Reconciliation

---

### RC-S01: GetById leaks reconciliation record existence across tenants via 403 vs 404 response
- **Severity**: Medium
- **Location**: `OpsReconciliationController.cs:217-229`
- **Trace**: Portal → `GET /api/v1/ops/reconciliation/{id}` → fetch with `IgnoreQueryFilters()` → access check → 403 vs 404
- **Description**: The `GetById` endpoint first fetches the reconciliation record by GUID with `IgnoreQueryFilters()` (bypassing tenant scoping), then checks `access.CanAccess(record.LegalEntityId)`. If the record exists but belongs to another tenant, the response is `Forbid()` (403). If it doesn't exist at all, the response is `NotFound()` (404). This difference allows an authenticated portal user to probe whether a specific reconciliation GUID exists in any tenant. Same pattern as TX-S03.
- **Impact**: Cross-tenant record existence oracle. While reconciliation IDs are server-generated GUIDs (hard to guess), a user with a GUID from logs or shared reports can confirm its existence.
- **Fix**: Return 404 for both not-found and forbidden: `if (record is null || !access.CanAccess(record.LegalEntityId)) return NotFound(...)`.

### RC-S02: No concurrent review protection — contradictory audit events can be published
- **Severity**: Medium
- **Location**: `ReviewReconciliationHandler.cs:56-105`
- **Trace**: Two concurrent POST requests → both load VARIANCE_FLAGGED → both publish events → last-write-wins
- **Description**: Without optimistic concurrency (see RC-T02), two simultaneous approve/reject requests both pass the `Status == VARIANCE_FLAGGED` check and publish their respective domain events (`ReconciliationApproved` + `ReconciliationRejected`). Both events enter the outbox and are published to downstream consumers. The final DB state reflects only the last save. This creates a contradictory audit trail where both an approval and rejection event exist for the same record, but the record shows only one outcome.
- **Impact**: Compliance and audit integrity risk. Downstream systems consuming these events (e.g., financial reporting, Odoo sync) may process conflicting actions. The audit trail becomes unreliable for dispute resolution.
- **Fix**: Add optimistic concurrency (see RC-T02). On concurrency conflict, retry or return 409.

### RC-S03: Review endpoint does not enforce reason length bounds — truncation or DB error possible
- **Severity**: Low
- **Location**: `ReviewReconciliationHandler.cs:32-37`, `ReconciliationRecordConfiguration.cs:45`
- **Trace**: API → handler → `reason.Trim()` → save → `review_reason VARCHAR(1000)`
- **Description**: The handler validates that the reason is non-empty after trimming, but enforces no minimum length (frontend requires 10 chars, backend does not) and no maximum length (DB column is 1000 chars). A direct API caller can submit a 1-character reason. Submitting >1000 characters causes a PostgreSQL error (unhandled, returns 500). The 500 response exposes the global exception handler's error format, which may include stack trace details in non-production environments.
- **Impact**: Low — the 1-char minimum bypass weakens audit quality; the >1000 char case produces an unfriendly error.
- **Fix**: Add length validation: `reason.Length < 10 || reason.Length > 1000` → return validation error.

### RC-S04: GetExceptions does not validate `legalEntityId` query parameter format before DB lookup
- **Severity**: Low
- **Location**: `OpsReconciliationController.cs:40-41`
- **Trace**: Portal → `GET /api/v1/ops/reconciliation/exceptions?legalEntityId=X` → `Guid?` binding
- **Description**: The `legalEntityId` parameter is bound as `Guid?`. If a non-GUID value is passed, ASP.NET Core model binding silently sets it to `null` (since it's nullable). With `legalEntityId=null`, the `ForPortal` extension applies the user's full legal entity scope instead of the requested single entity. A user who typos the legal entity ID would silently see data across all their legal entities instead of getting a validation error.
- **Impact**: User confusion — requesting a specific entity with a malformed ID returns unscoped results. No data leakage since tenant scoping still applies.

## Module: PreAuthorization (Odoo POS → Edge Agent → FCC Device → Cloud)

---

### PA-S01: PreAuthExpiryWorker uses IgnoreQueryFilters — bypasses tenant scoping for batch expiry
- **Severity**: High
- **Location**: `PreAuthExpiryWorker.cs:78`
- **Trace**: PreAuthExpiryWorker → `ExpireBatchAsync()` → `db.PreAuthRecords.IgnoreQueryFilters()` → cross-tenant query
- **Description**: The expiry worker queries pre-auth records with `.IgnoreQueryFilters()`, which bypasses the global tenant isolation query filter applied by `TenantScopeMiddleware`. This means the worker processes pre-auth records across ALL legal entities in a single batch of up to 500 records. While the worker is a trusted backend process and the operation (expire + deauth) is correct regardless of tenant, this violates the principle of tenant isolation. A bug in the expiry logic, an incorrect state transition, or a failed deauth call would affect records across all tenants simultaneously. Additionally, the deauthorization calls in `TryDeauthorizePumpAsync` (line 114) use `ISiteFccConfigProvider.GetBySiteCodeAsync` which may also be tenant-scoped, creating a potential mismatch.
- **Impact**: A single bug in the expiry worker could corrupt pre-auth records across all tenants. Failed deauth for one tenant's FCC could cascade delays to other tenants' records in the same batch.
- **Recommendation**: Either process records per-tenant (query with tenant context) or add explicit tenant ID logging to each expired record for audit traceability.

### PA-S02: UpdatePreAuthStatusRequest and PreAuthForwardRequest lack max-length validation — oversized strings cause raw DB errors
- **Severity**: Medium
- **Location**: `UpdatePreAuthStatusRequest.cs:1-17`, `PreAuthForwardRequest.cs:13-34`
- **Trace**: Edge Agent → `POST/PATCH /api/v1/preauth` → request body → controller → handler → EF `SaveChangesAsync()` → PostgreSQL column length violation
- **Description**: `UpdatePreAuthStatusRequest` has no `[StringLength]` or `[MaxLength]` attributes on any of its string fields: `FccCorrelationId`, `FccAuthorizationCode`, `FailureReason`, `MatchedFccTransactionId`. The database columns have strict max lengths (200, 200, 500, 256 respectively per `PreAuthRecordConfiguration.cs:39-55`). `PreAuthForwardRequest` has `[StringLength]` only on `CustomerTaxId` (100) and `CustomerBusinessName` (200) but not on `SiteCode` (DB: 50), `OdooOrderId` (DB: 200), `ProductCode` (DB: 50), `Currency` (DB: 3), `VehicleNumber` (DB: 50), or `AttendantId` (DB: 100). An edge agent sending oversized strings would bypass model validation and cause a raw PostgreSQL error (`22001: value too long for type character varying`) surfaced as a 500 response.
- **Impact**: Unvalidated strings cause unhandled database exceptions, returning raw 500 errors instead of clean 400 validation responses.
- **Recommendation**: Add `[MaxLength]` attributes matching the database column lengths to all string properties on both request DTOs.

### PA-S03: CustomerTaxId stored in plaintext in PostgreSQL and edge SQLite databases — no field-level encryption
- **Severity**: Medium
- **Location**: Cloud `PreAuthRecordConfiguration.cs:44`, Android `PreAuthRecord.kt:94`, Desktop `BufferedPreAuth.cs:53`
- **Trace**: Odoo POS → Edge Agent (Room/SQLite: plaintext) → Cloud Forward → PostgreSQL `pre_auth_records.customer_tax_id` (plaintext)
- **Description**: The `CustomerTaxId` field is marked with `[Sensitive]` attribute in the domain entity (`PreAuthRecord.cs:52`) and the contract DTO (`PreAuthForwardRequest.cs:30`), and the code comments warn "PII — never log." However, no actual encryption is applied at the storage layer. The field is stored as plaintext `VARCHAR(100)` in PostgreSQL and plaintext `TEXT` in both Android Room and Desktop SQLite. In contrast, the site FCC configuration module uses `AesGcmFieldEncryptor` with an EF `EncryptedFieldConverter` for sensitive FCC connection credentials, demonstrating that the infrastructure for field-level encryption exists but is not applied to PII fields.
- **Impact**: Customer Tax Identification Numbers (TINs) are stored in plaintext across three database systems. A database breach or backup leak would expose PII subject to tax authority regulations.
- **Recommendation**: Apply the existing `AesGcmFieldEncryptor` / `EncryptedFieldConverter` pattern to `CustomerTaxId` (and optionally `CustomerName`) in the cloud PostgreSQL database. On edge agents, consider Android Keystore-backed encryption and SQLCipher or EF encrypted columns respectively.

### PA-S04: FCC internal error details and Java exception class names exposed in pre-auth API responses
- **Severity**: Medium
- **Location**: Android `PreAuthHandler.kt:216-228`, Desktop `PreAuthHandler.cs:254, 262`
- **Trace**: FCC device → adapter exception → PreAuthHandler → `PreAuthResult.message` → Odoo POS API response
- **Description**: Android `PreAuthHandler.kt` constructs error messages that include Java exception class names and FCC internal details: `"FCC_NETWORK_ERROR: ${e.javaClass.simpleName}"` (line 222), `"FCC_INTERNAL_ERROR: ${e.javaClass.simpleName}"` (line 227). These messages propagate through `PreAuthResult.message` to the API response returned to Odoo POS. Desktop `PreAuthHandler.cs:254` stores `fccResult.ErrorCode ?? "FCC_DECLINED"` as the failure reason and line 262 logs `record.FccAuthorizationCode`. While the callers are trusted (Odoo POS on LAN), the error messages reveal internal implementation details (Java class names, timeout values, connection states) that could aid reconnaissance if intercepted.
- **Impact**: Internal technology stack details (Java class names, adapter timeout values, connectivity state enums) are exposed to LAN callers.
- **Recommendation**: Return generic error codes to callers (e.g., `FCC_NETWORK_ERROR`, `FCC_TIMEOUT`) without implementation details. Log the detailed exception information server-side only.

### PA-S05: Cloud ExpiryWorker FCC deauthorization uses cloud-side adapter to call FCC devices — expands attack surface
- **Severity**: Low
- **Location**: `PreAuthExpiryWorker.cs:129-172`
- **Trace**: PreAuthExpiryWorker → `ISiteFccConfigProvider.GetBySiteCodeAsync()` → `IFccAdapterFactory.Resolve()` → `IFccPumpDeauthorizationAdapter.DeauthorizePumpAsync()` → FCC device
- **Description**: The cloud-side expiry worker attempts FCC pump deauthorization by resolving a site config and creating an FCC adapter from the cloud. The cloud adapters (`DomsCloudAdapter`, `RadixCloudAdapter`, etc.) are designed as inbound webhook receivers, not outbound callers. If `IFccPumpDeauthorizationAdapter` is implemented as an outbound call from cloud to FCC devices, this introduces a new network path (cloud → FCC device LAN) that doesn't exist elsewhere in the architecture. The FCC vendor credentials stored in `fcc_configs` would be used for outbound calls from the cloud, potentially exposing them to different network segments.
- **Impact**: A new outbound call path from cloud to FCC devices may bypass existing network segmentation and firewall rules designed for the inbound-only cloud adapter pattern.
- **Recommendation**: Verify whether `IFccPumpDeauthorizationAdapter` makes outbound calls to FCC devices. If so, consider delegating deauthorization to the edge agent via a command queue rather than calling FCC directly from the cloud.

---

## Module: Onboarding (Registration & Provisioning)

---

### OB-S01: Desktop agent does not purge device/refresh tokens from credential store on decommission
- **Severity**: Medium
- **Location**: `RegistrationManager.cs:116-123` (`MarkDecommissionedAsync`)
- **Trace**: Cloud returns 403 DEVICE_DECOMMISSIONED → `DeviceTokenProvider.RefreshTokenAsync()` throws `DeviceDecommissionedException` → caller invokes `RegistrationManager.MarkDecommissionedAsync()` → sets `IsDecommissioned = true` → saves registration.json
- **Description**: When the desktop agent is decommissioned, `MarkDecommissionedAsync` sets the `IsDecommissioned` flag and saves the state file, but does NOT call `ICredentialStore.DeleteSecretAsync()` for the device token (`device:token`) or refresh token (`device:refresh_token`). The server-side revocation makes the tokens unusable, but the plaintext/encrypted token values remain in the platform credential store (DPAPI on Windows, Keychain on macOS, libsecret/encrypted file on Linux). In contrast, the Android agent's `DecommissionedActivity.startReProvisioning()` explicitly calls `keystoreManager.clearAll()` to purge all Keystore entries, and `encryptedPrefs.clearAll()` to wipe identity data. The desktop's `MarkReprovisioningRequiredAsync` has the same gap.
- **Impact**: Revoked device JWTs and refresh tokens persist in the desktop credential store after decommission. If the credential store is later compromised (backup theft, forensic analysis, malware), the attacker obtains tokens that, while revoked, reveal the device ID, site code, and legal entity ID embedded in the JWT claims (JWTs are signed, not encrypted). Additionally, a revoked refresh token presented to the server triggers the reuse detection path (BUG-010), creating noise in audit logs.
- **Fix**: Add credential purging to `MarkDecommissionedAsync` and `MarkReprovisioningRequiredAsync`: call `ICredentialStore.DeleteSecretAsync(CredentialKeys.DeviceToken)` and `ICredentialStore.DeleteSecretAsync(CredentialKeys.RefreshToken)` before saving the state file.

### OB-S02: Desktop Linux PlatformCredentialStore AES fallback derives encryption key from world-readable machine-id
- **Severity**: Medium
- **Location**: `PlatformCredentialStore.cs` — Linux `EncryptedFileCredentialStore` inner class
- **Trace**: Linux desktop agent → `PlatformCredentialStore` → libsecret unavailable → falls back to `EncryptedFileCredentialStore` → `DeriveKey()` → `PBKDF2(machine-id, fixedSalt, 100_000, 32)`
- **Description**: When `secret-tool` (libsecret) is not available on Linux (e.g., headless servers, minimal desktop environments, Docker containers), the `PlatformCredentialStore` falls back to an AES-256 encrypted file stored at `{AgentDataDirectory}/secrets/credentials.dat`. The encryption key is derived via PBKDF2 with `/etc/machine-id` as the password and a fixed salt embedded in the source code. On most Linux distributions, `/etc/machine-id` is world-readable (permissions 0444). Any local user — or any process running on the same machine — can read the machine-id, derive the identical encryption key, and decrypt the credential file to extract the device JWT, refresh token, and FCC API keys in plaintext.
- **Impact**: On Linux systems using the fallback credential store, all stored secrets (device JWT, refresh token, FCC credentials, LAN API key) are accessible to any local user. This defeats the purpose of credential encryption for multi-user systems or systems where other untrusted services run.
- **Recommendation**: Use a machine-specific secret combined with a per-installation random salt (generated on first run and stored alongside the encrypted file) to make key derivation non-deterministic. Alternatively, require libsecret as a mandatory dependency on Linux and fail with a clear error message rather than falling back to weak encryption.

### OB-S03: Registration endpoint accepts provisioning token from both request body and HTTP header — header-channel tokens at higher logging risk
- **Severity**: Low
- **Location**: `AgentController.cs:177-183`
- **Trace**: Edge Agent → `POST /api/v1/agent/register` → body `provisioningToken` or `X-Provisioning-Token` header → controller extracts token
- **Description**: The registration endpoint accepts the provisioning token from the JSON body (`request.ProvisioningToken`, line 178), falling back to the `X-Provisioning-Token` HTTP header (line 180) if the body field is empty. HTTP headers are routinely logged by reverse proxies (nginx, AWS ALB, CloudFront), CDNs, API gateways, and application-level request logging middleware. Request bodies are less commonly logged. The `[Sensitive]` attribute on the DTO's `ProvisioningToken` property prevents the application's own logging from recording the value, but cannot control upstream infrastructure logging of HTTP headers. If an edge agent sends the token via header instead of body, the raw bootstrap token may appear in access logs, proxy logs, or WAF logs.
- **Impact**: Bootstrap tokens transmitted via HTTP header are at higher risk of appearing in infrastructure logs. A leaked token allows a rogue device to register at the target site within the token's 72-hour window.
- **Recommendation**: Deprecate the `X-Provisioning-Token` header path. Both the Android and desktop agents send the token in the request body. If the header fallback exists for legacy compatibility, document it as deprecated and add a log warning when the header path is used.

### OB-S04: Decommission endpoint leaks device existence across tenants via 403 vs 404 response difference
- **Severity**: Low
- **Location**: `AgentController.cs:325-336`
- **Trace**: Portal → `POST /api/v1/admin/agent/{deviceId}/decommission` → fetch with `IgnoreQueryFilters()` → access check → `Forbid()` (403) vs `NotFound()` (404)
- **Description**: The decommission endpoint fetches the device by GUID with `IgnoreQueryFilters()` (bypassing tenant scoping), then checks `access.CanAccess(device.LegalEntityId)`. If the device exists but belongs to another tenant, the response is `Forbid()` (403). If the device doesn't exist, the response is `NotFound()` (404). This is the same cross-tenant existence oracle pattern identified in TX-S03 and RC-S01. While the endpoint requires `PortalAdminWrite` authorization (limiting the attacker pool to admin users), and device IDs are random GUIDs (hard to guess), the response difference still reveals cross-tenant information.
- **Impact**: An authenticated admin user can probe whether a specific device GUID exists in any tenant by observing 403 vs 404. Combined with a GUID obtained from shared logs, support tickets, or screenshots, this confirms the existence of devices in other tenants.
- **Fix**: Return 404 for both not-found and forbidden: `if (device is null || !access.CanAccess(device.LegalEntityId)) return NotFound(...)`.
