# Android Security Findings — FCC Edge Agent

**Module**: FCC Edge Agent (Android)
**Audit date**: 2026-03-13
**Scope**: End-to-end trace — UI → State → Workers → Adapters → DB/Network

---

## AS-001: FCC Access Code Stored in EncryptedSharedPreferences — Not in Keystore

| Field | Value |
|-------|-------|
| **ID** | AS-001 |
| **Title** | FCC access codes saved via LocalOverrideManager use EncryptedSharedPreferences instead of Android Keystore |
| **Module** | Site Configuration / Security |
| **Severity** | Medium |
| **Category** | Insecure Token Storage |
| **Description** | `SettingsActivity.saveAndReconnect()` saves the FCC access code via `localOverrideManager.saveOverride(LocalOverrideManager.KEY_FCC_CREDENTIAL, fccAccessCode)` (line 255). `LocalOverrideManager` uses `EncryptedSharedPreferences` which encrypts at rest with AES-256-SIV. However, the FCC access code is a sensitive credential that controls physical fuel pump operations. The cloud-delivered credential uses a `secretEnvelope` pattern with Keystore-backed encryption (per `ConfigManager` M-13). Local overrides bypass this and use the less secure EncryptedSharedPreferences. On rooted devices, `EncryptedSharedPreferences` keys can be extracted from the Keystore master key. |
| **Evidence** | `ui/SettingsActivity.kt` line 255: `localOverrideManager.saveOverride(LocalOverrideManager.KEY_FCC_CREDENTIAL, fccAccessCode)`. `config/LocalOverrideManager.kt` uses `EncryptedSharedPreferences`. |
| **Impact** | FCC access codes set by technicians are stored with weaker protection than cloud-delivered credentials. On compromised devices, the access code could be extracted and used to control fuel pumps. |
| **Recommended Fix** | Store the FCC access code in the Android Keystore via `KeystoreManager.storeSecret()`, consistent with the cloud-delivered credential storage pattern. |

---

## AS-002: QR Code Provisioning Token Logged (Indirectly via QrBootstrapData)

| Field | Value |
|-------|-------|
| **ID** | AS-002 |
| **Title** | QR bootstrap data creation is logged at INFO level — token could leak via stack traces |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Low |
| **Category** | Sensitive Data Logged |
| **Description** | `QrBootstrapData` correctly overrides `toString()` to redact the token (line 598–600). However, `ProvisioningActivity.onScanResult()` at line 249 logs `"QR code scanned, parsing payload"` at INFO level. If an exception occurs during parsing and the raw JSON is included in the stack trace or a future debug log, the provisioning token would be exposed. Additionally, `ProvisioningViewModel.register()` receives the `QrBootstrapData` which contains the unredacted `provisioningToken` — any exception handler that serializes the exception context (e.g., Sentry, Crashlytics) could capture it. |
| **Evidence** | `ui/ProvisioningActivity.kt` line 249: `AppLogger.i(TAG, "QR code scanned, parsing payload")`. `ui/ProvisioningActivity.kt` line 354: exception handler logs full error. |
| **Impact** | Low risk: the `toString()` override mitigates direct logging, but indirect exposure through crash reporters is still possible. |
| **Recommended Fix** | Ensure the `provisioningToken` field is annotated with a `@Sensitive` marker that the crash reporter's PII filter strips. Verify that no exception handler serializes the `QrBootstrapData` object. |

---

## AS-003: Local API Server Uses Plain HTTP for LAN Mode — No TLS

| Field | Value |
|-------|-------|
| **ID** | AS-003 |
| **Title** | LAN API mode exposes transaction data and API keys over plain HTTP |
| **Module** | Local API |
| **Severity** | High |
| **Category** | Missing API Auth Headers / Insecure Channel |
| **Description** | When `enableLanApi = true`, `LocalApiServer` binds to `0.0.0.0:8585` with plain HTTP (no TLS). The `X-Api-Key` header is transmitted in cleartext. Any device on the same network segment can sniff the API key, transaction data (including amounts, pump numbers, product codes), and pre-auth requests (including `customerTaxId`). The code comments acknowledge this risk (lines 49–51) but do not enforce any mitigation. |
| **Evidence** | `api/LocalApiServer.kt` lines 86, 103, 138: CIO engine with no TLS configuration. Line 384: `X-Api-Key` sent in cleartext. |
| **Impact** | API key and all transaction/pre-auth data visible to any device on the same LAN segment. Enables replay attacks where a captured API key is reused to send fraudulent pre-auth requests. |
| **Recommended Fix** | Add TLS support to the embedded Ktor server using a self-signed certificate or a certificate provisioned from the cloud. Alternatively, use mutual TLS (mTLS) for LAN authentication. At minimum, document that LAN mode must only be used on physically isolated networks with MAC-level access control. |

---

## AS-004: Certificate Pins Currently Empty — No Effective Pinning

| Field | Value |
|-------|-------|
| **ID** | AS-004 |
| **Title** | Bootstrap certificate pins are populated but network_security_config has placeholder pins |
| **Module** | Cloud Sync / Security |
| **Severity** | Medium |
| **Category** | Missing API Auth Headers |
| **Description** | The `AppModule.kt` (lines 110–113) defines `bootstrapPins` with two SHA-256 pin hashes. However, the `network_security_config.xml` referenced in `AndroidManifest.xml` has a "pin-set placeholder for future certificate pinning" (per the exploration). If the XML pin-set is empty or contains placeholder values, Android's network security config does not enforce pinning. The OkHttp-level `CertificatePinner` in `HttpCloudApiClient` may enforce pins, but any other HTTP client in the app (e.g., Ktor clients for Radix/Petronite adapters) would not be covered. |
| **Evidence** | `di/AppModule.kt` lines 110–113: bootstrap pins defined. `AndroidManifest.xml`: references `network_security_config`. Exploration report: "Pin-set placeholder for future certificate pinning." |
| **Impact** | MITM attacks on the cloud API may not be prevented if the OkHttp pinning is bypassed or if other HTTP clients are used. |
| **Recommended Fix** | Populate `network_security_config.xml` with actual certificate pins matching the bootstrap pins in `AppModule`. Ensure all HTTP clients (including Ktor clients for FCC adapters) use pinned connections for cloud traffic. |

---

## AS-005: FCC Adapter HTTP Clients Do Not Use TLS for LAN Traffic

| Field | Value |
|-------|-------|
| **ID** | AS-005 |
| **Title** | Advatec and Petronite adapters communicate with FCC over plain HTTP on LAN |
| **Module** | FCC Adapters |
| **Severity** | Medium |
| **Category** | Insecure Channel |
| **Description** | `AdvatecAdapter` uses `http://{host}:{port}/api/v2/incoming` (plain HTTP) to communicate with the Advatec device. `PetroniteAdapter` uses a configurable base URL that may or may not use HTTPS depending on the FCC device's capabilities. DOMS uses raw TCP. While these are LAN connections to physical hardware, the traffic includes FCC access codes, transaction data, and pre-auth commands. On a compromised LAN, this data is visible. |
| **Evidence** | Adapter exploration: Advatec uses `POST http://{host}:{port}/api/v2/incoming`. Petronite uses `POST /oauth/token` and REST APIs. |
| **Impact** | FCC access codes and pre-auth commands visible on the LAN. Pump control commands could be intercepted and replayed. |
| **Recommended Fix** | For Advatec and Petronite, use HTTPS when the FCC device supports it. For DOMS TCP, consider TLS wrapping. At minimum, ensure FCC LAN traffic is on a physically isolated VLAN. |

---

## AS-006: Radix SHA-1 Signature Is Cryptographically Weak

| Field | Value |
|-------|-------|
| **ID** | AS-006 |
| **Title** | RadixSignatureHelper uses SHA-1 for message authentication — collision-prone |
| **Module** | FCC Adapters (Radix) |
| **Severity** | Medium |
| **Category** | Insecure Cryptographic Primitives |
| **Description** | `RadixSignatureHelper` uses SHA-1 to sign and verify Radix protocol messages. SHA-1 has been broken for collision resistance (SHAttered attack, 2017) and is deprecated by NIST for all digital signature use. While the Radix protocol mandates SHA-1 (vendor constraint), the agent should log a security warning and document the limitation. |
| **Evidence** | `adapter/radix/RadixSignatureHelper.kt`: SHA-1 usage for message authentication. |
| **Impact** | A sophisticated LAN attacker could forge Radix messages (transaction data, pre-auth responses) by crafting SHA-1 collisions. |
| **Recommended Fix** | This is a vendor protocol constraint and cannot be changed unilaterally. Document the risk in the security assessment. Request the vendor to migrate to SHA-256. Add a runtime security warning log. |

---

## AS-007: AdvatecWebhookListener Always Returns 200 OK — No Request Validation

| Field | Value |
|-------|-------|
| **ID** | AS-007 |
| **Title** | Advatec webhook accepts all requests with HTTP 200 even when token validation fails silently |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Medium |
| **Category** | Missing API Auth Headers |
| **Description** | `AdvatecWebhookListener` validates `X-Webhook-Token` or `?token=` from incoming webhook requests. However, based on the exploration, it "always responds 200 OK." If the token validation fails, the request should be rejected with 401/403, not accepted with 200. A malicious device on the LAN could inject fake transaction receipts by sending webhook requests without a valid token. |
| **Evidence** | Adapter exploration report: "Webhook always returns 200 OK." `adapter/advatec/AdvatecWebhookListener.kt`. |
| **Impact** | Fake transactions could be injected into the buffer, causing incorrect financial records. |
| **Recommended Fix** | Return HTTP 401 when the webhook token is missing or invalid. Only return 200 after successful token validation and payload processing. |

---

## AS-008: customerTaxId Stored in PreAuthRecord Without Encryption

| Field | Value |
|-------|-------|
| **ID** | AS-008 |
| **Title** | Customer tax ID (PII) stored in plaintext in Room database |
| **Module** | Pre-Authorization |
| **Severity** | Medium |
| **Category** | Sensitive Data Handling |
| **Description** | `PreAuthHandler.handle()` stores `command.customerTaxId` directly into the `PreAuthRecord` entity (line 169) which is persisted to the Room SQLite database in plaintext. The code comment says "PII — NEVER log" but the value is stored unencrypted in the database. On a rooted device or via ADB backup, the database file can be read, exposing customer tax identification numbers. |
| **Evidence** | `preauth/PreAuthHandler.kt` line 169: `customerTaxId = command.customerTaxId, // PII — NEVER log`. |
| **Impact** | Customer PII (tax ID) exposed in plaintext database storage. May violate GDPR, local data protection laws, and the project's own PII handling policy. |
| **Recommended Fix** | Encrypt `customerTaxId` using `KeystoreManager.storeSecret()` before persisting. Decrypt only when building the cloud forward payload. Alternatively, do not persist the tax ID locally — forward it to cloud immediately and discard. |

---

## AS-009: network_security_config Allows Cleartext Traffic Only to Specific Domains

| Field | Value |
|-------|-------|
| **ID** | AS-009 |
| **Title** | Network security config correctly disables cleartext but FCC LAN traffic bypasses it |
| **Module** | Security |
| **Severity** | Low |
| **Category** | Insecure Network Configuration |
| **Description** | `AndroidManifest.xml` references `network_security_config.xml` which disables cleartext traffic globally. However, the Advatec adapter uses `http://` to communicate with the FCC device on localhost/LAN. Since the Ktor HTTP client for FCC traffic does not go through Android's network stack validation (it uses `OkHttpClient` or `CIO` engine directly), the network security config's cleartext prohibition is bypassed. This is architecturally correct (FCC LAN traffic must be cleartext for compatibility), but the bypass should be explicitly documented. |
| **Evidence** | `AndroidManifest.xml`: `networkSecurityConfig`. Adapter exploration: Advatec uses `http://`. |
| **Impact** | Documentation gap: security auditors may flag the cleartext FCC traffic as a violation of the network security config. |
| **Recommended Fix** | Document in the network security config XML (as a comment) that FCC LAN traffic intentionally uses cleartext and explain why. Add domain exceptions for known FCC IP ranges if using Android's HTTP stack. |

---

## AS-010: ProvisioningViewModel Logs deviceId and siteCode at INFO Level

| Field | Value |
|-------|-------|
| **ID** | AS-010 |
| **Title** | Registration success log includes device identity data |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Low |
| **Category** | Sensitive Data Logged |
| **Description** | `ProvisioningViewModel.handleRegistrationSuccess()` at line 127 logs `"Registration successful: deviceId=${response.deviceId}, site=${response.siteCode}"` at INFO level. While `deviceId` and `siteCode` are not PII, they are device-identifying information that could be used for targeted attacks if log files are exfiltrated. The structured file logger writes to persistent files that can be shared via the diagnostics screen. |
| **Evidence** | `ui/ProvisioningViewModel.kt` line 127: `AppLogger.i(TAG, "Registration successful: deviceId=${response.deviceId}, site=${response.siteCode}")`. |
| **Impact** | Low risk: device identity visible in persistent log files. |
| **Recommended Fix** | Truncate the `deviceId` in log output (e.g., first 8 characters). |

---

## AS-011: Initial Device Registration Call Bypasses Certificate Pinning

| Field | Value |
|-------|-------|
| **ID** | AS-011 |
| **Title** | POST /api/v1/agent/register is sent over unpinned HTTPS — provisioning token exposed to MITM |
| **Module** | Provisioning & Lifecycle |
| **Severity** | High |
| **Category** | Missing API Auth Headers |
| **Description** | `HttpCloudApiClient.registerDevice()` at line 519 constructs the URL from the `cloudBaseUrl` parameter (the QR-provided URL, e.g., `https://api.fccmiddleware.io`), not from the instance's `this.cloudBaseUrl`. The instance's `this.cloudBaseUrl` is `"https://not-yet-provisioned"` before registration, and the OkHttp `CertificatePinner` only has pins bound to the `not-yet-provisioned` hostname (see AT-016). OkHttp's `CertificatePinner` only enforces pinning for hostnames that appear in its pin set — for any other hostname, connections proceed without pinning. This means the registration call to `api.fccmiddleware.io` is sent over standard HTTPS with no certificate pinning. The `DeviceRegistrationRequest` body contains the one-time `provisioningToken` — a bearer credential that authorizes device registration. A MITM attacker with a trusted CA certificate (e.g., corporate proxy, compromised CA) can intercept this token and register a rogue device, gaining access to the site's transaction data and FCC control commands. |
| **Evidence** | `sync/CloudApiClient.kt` line 519: `httpClient.post("$cloudBaseUrl/api/v1/agent/register")` — uses parameter `cloudBaseUrl`, not `this.cloudBaseUrl`. `sync/CloudApiClient.kt` lines 686–698: CertificatePinner bound to `extractHostname(this.cloudBaseUrl)` = `"not-yet-provisioned"`. OkHttp docs: "Pinning is per-hostname. Unpinned hostnames are allowed." |
| **Impact** | One-time provisioning token can be intercepted by a MITM attacker. Rogue device registration grants access to site-scoped cloud APIs (transactions, pre-auth, config, telemetry). The provisioning token is single-use, so the legitimate device's registration would fail with CONFLICT if the rogue registers first. |
| **Recommended Fix** | Build a separate `OkHttpClient` for the registration call that pins to the known cloud hostnames (from `CloudEnvironments`). Alternatively, add all known cloud hostnames to the initial CertificatePinner (not just the stub URL). The `network_security_config.xml` should also be populated with real pins to provide system-level pinning as a fallback. |

---

## AS-012: Config Encryption Fallback Stores Raw SiteConfig JSON in Room Database

| Field | Value |
|-------|-------|
| **ID** | AS-012 |
| **Title** | When Keystore encryption fails, raw config JSON is persisted unencrypted in agent_config table |
| **Module** | Provisioning & Lifecycle |
| **Severity** | Low |
| **Category** | Insecure Token Storage |
| **Description** | `ProvisioningViewModel.handleRegistrationSuccess()` at lines 174–179 encrypts the config JSON with `keystoreManager.storeSecret()`. If encryption fails (returns null), the fallback at line 178 stores the raw JSON directly: `rawConfigJson`. The raw JSON may contain FCC connection parameters (host, port, vendor), sync settings (cloudBaseUrl), and identity data (siteCode, deviceId). While FCC credentials use a `secretEnvelope` pattern and are not present in the raw config, the connection parameters and cloud URL can aid targeted attacks. The Room database file is accessible on rooted devices or via ADB backup (though `android:allowBackup="false"` is set in the manifest). The code logs a warning but continues operating with unencrypted config. |
| **Evidence** | `ui/ProvisioningViewModel.kt` lines 174–179: `if (encryptedBytes != null) { "ENC:" + ... } else { AppLogger.w(...); rawConfigJson }`. |
| **Impact** | Low: the config does not contain plaintext credentials (they use a separate secretEnvelope). Connection parameters and identity data are exposed in the Room database on compromised devices. |
| **Recommended Fix** | Treat Keystore encryption failure as a fatal error for config persistence — skip storing the config and let the service fetch it on first poll. Alternatively, retry encryption once after a short delay (Keystore may be temporarily unavailable during boot). |

---

## AS-013: Customer Tax ID Sent in Plaintext HTTP to Advatec Device

| Field | Value |
|-------|-------|
| **ID** | AS-013 |
| **Title** | PII field customerTaxId transmitted over plain HTTP to localhost fiscal device |
| **Module** | FCC Adapters (Advatec) |
| **Severity** | Medium |
| **Category** | Sensitive Data Handling |
| **Description** | `AdvatecAdapter.sendPreAuth()` includes `command.customerTaxId` (annotated `@Sensitive` and documented as "PII — NEVER log") in the JSON body sent via plain HTTP to `http://127.0.0.1:5560/api/v2/incoming`. The same occurs in `AdvatecFiscalizationService.submitForFiscalization()`. While traffic to localhost does not traverse the network, the Advatec device address is configurable (`config.advatecDeviceAddress`) and may be set to a LAN IP (e.g., `192.168.1.100`) in deployments where the EFD is a separate device. In that case, the customer tax ID — a government-issued personal identifier — is transmitted in cleartext on the LAN. Additionally, on rooted devices, localhost traffic can be captured by on-device packet sniffers. |
| **Evidence** | `adapter/advatec/AdvatecAdapter.kt` line 473: `customerId = command.customerTaxId ?: ""`. Line 479: `url = "http://$host:$port/api/v2/incoming"`. `adapter/advatec/AdvatecFiscalizationService.kt` line 107: same pattern. |
| **Impact** | Customer PII (tax ID) exposed in cleartext on the wire when Advatec device is on LAN. Violates TRA data protection requirements and the project's own `@Sensitive` annotation policy. |
| **Recommended Fix** | Use HTTPS for Advatec communication when the device supports it. For localhost deployments, document that the device must run on the same physical device. For LAN deployments, require TLS or VPN. At minimum, log a security warning when `advatecDeviceAddress` is not `127.0.0.1` or `localhost`. |

---

## AS-014: Radix Auth Response Signature Not Validated on Pre-Auth Calls

| Field | Value |
|-------|-------|
| **ID** | AS-014 |
| **Title** | sendPreAuth and cancelPreAuth parse ACKCODE without validating SHA-1 signature — responses can be forged |
| **Module** | FCC Adapters (Radix) |
| **Severity** | Medium |
| **Category** | Missing API Auth Headers |
| **Description** | `RadixAdapter.sendPreAuth()` at line 937 calls `RadixXmlParser.parseAuthResponse()` to extract the ACKCODE, but never calls `RadixXmlParser.validateAuthResponseSignature()` to verify the SHA-1 signature. Similarly, `cancelPreAuth()` at line 1078 parses without validating. In contrast, `RadixPushListener.handlePushRequest()` at lines 241–253 correctly validates the transaction response signature before accepting pushed data. A LAN attacker who can intercept traffic between the edge agent and the FCC (same LAN segment) could forge a pre-auth response with `ACKCODE=0` (success). The agent would then believe the pump is authorized and track a phantom pre-auth entry. The POS would display "pump authorized" to the customer, but the physical pump would not dispense fuel, creating an operational deadlock requiring manual intervention. |
| **Evidence** | `adapter/radix/RadixAdapter.kt` lines 937–951: `parseAuthResponse` without `validateAuthResponseSignature`. Compare with `adapter/radix/RadixPushListener.kt` lines 241–253: `validateTransactionResponseSignature` IS called. |
| **Impact** | Forged pre-auth responses on a compromised LAN could create phantom pump authorizations, causing POS/pump state mismatch and operational confusion. |
| **Recommended Fix** | Add signature validation to `sendPreAuth()` and `cancelPreAuth()`: call `RadixXmlParser.validateAuthResponseSignature(responseBody, sharedSecret)` before parsing. On validation failure, return `PreAuthResultStatus.ERROR` with a message indicating signature mismatch. |

---

## AS-015: FCC Access Code Passed Through JplMessage Data Map Without Sensitive Filtering

| Field | Value |
|-------|-------|
| **ID** | AS-015 |
| **Title** | DOMS FcLogon access code stored in unprotected Map — exposed if JplMessage is serialized or logged |
| **Module** | FCC Adapters (DOMS) |
| **Severity** | Low |
| **Category** | Sensitive Data Logged |
| **Description** | `DomsLogonHandler.buildLogonRequest()` places the FCC access code into a `JplMessage.data` map with key `"FcAccessCode"`. The `JplMessage` class is annotated with `@Serializable` and its `data` field is `Map<String, String>` — it has no `@Sensitive` annotation and no `toString()` override that redacts the access code. If `JplTcpClient.sendAndReceive()` or any error handler serializes the request `JplMessage` for logging (e.g., on send failure, the exception handler at `DomsJplAdapter.kt` line 129 logs `e.message` which may include the serialized request), the access code is written to the structured log file. The `AgentFccConfig.fcAccessCode` field IS properly annotated with `@Sensitive`, but this protection is lost when the value is copied into the unprotected map. |
| **Evidence** | `adapter/doms/protocol/DomsLogonHandler.kt` line 38: `"FcAccessCode" to fcAccessCode` in `JplMessage.data`. `adapter/doms/jpl/JplMessage.kt`: `@Serializable data class JplMessage(val name: String, val data: Map<String, String>)` — no sensitive filtering. |
| **Impact** | FCC access code could appear in log files if JplMessage is serialized during error handling. Log files are persistent and accessible via the diagnostics screen. |
| **Recommended Fix** | Override `toString()` in `JplMessage` to redact keys matching sensitive patterns (e.g., keys containing "AccessCode", "Secret", "Password"). Alternatively, add a `@Sensitive` annotation to `JplMessage.data` and ensure the `SensitiveFieldFilter` processes it. |

---

## AS-016: WebSocket Shared-Secret Comparison Is Not Timing-Safe

| Field | Value |
|-------|-------|
| **ID** | AS-016 |
| **Title** | OdooWebSocketServer validates shared secret with standard string inequality — vulnerable to timing side-channel |
| **Module** | Transaction Management / POS Integration |
| **Severity** | Low |
| **Category** | Insecure Token Storage |
| **Description** | `OdooWebSocketServer.onClientConnected()` validates the WebSocket shared secret using `if (provided != secret)` (standard Kotlin string inequality). Standard string comparison returns `false` as soon as the first differing character is found, making the comparison time proportional to the length of the common prefix. An attacker on the same LAN segment could use timing analysis to determine the shared secret character by character. The practical exploitation difficulty is high because: (1) the attacker must already be on the same LAN, (2) WebSocket upgrade latency introduces significant noise, and (3) the timing difference per character is typically <1 microsecond. However, the fix is trivial and closes the theoretical vulnerability. |
| **Evidence** | `websocket/OdooWebSocketServer.kt` line 146: `if (provided != secret)` — timing-variable comparison. |
| **Impact** | Theoretical: an attacker on the same LAN could recover the WebSocket shared secret through timing analysis, gaining the ability to inject commands (transaction updates, pump unblock) into the Odoo POS workflow. |
| **Recommended Fix** | Use `java.security.MessageDigest.isEqual(provided.toByteArray(), secret.toByteArray())` for constant-time comparison. Alternatively, hash both values with SHA-256 and compare the hashes. |

---

## AS-017: Transaction rawPayloadJson Stored Unencrypted in Room Database

| Field | Value |
|-------|-------|
| **ID** | AS-017 |
| **Title** | Raw FCC protocol payloads persisted in plaintext SQLite — may contain operational-sensitive data |
| **Module** | Transaction Management |
| **Severity** | Low |
| **Category** | Insecure File Handling |
| **Description** | `BufferedTransaction.rawPayloadJson` stores the complete raw FCC protocol payload as a plaintext TEXT column in the Room database. For DOMS, this is the JPL frame data including supervised buffer contents. For Radix, this is the XML response including station configuration details. For Advatec, this includes webhook payload with receipt line items and device-specific identifiers. While these payloads do not contain PII or credentials (those are in separate fields), they reveal operational details about the FCC hardware: firmware versions, protocol-specific IDs, station serial numbers, and pump configuration. On a rooted device or via ADB backup extraction, this information could aid a targeted attack on the FCC hardware. The `android:allowBackup="false"` manifest flag mitigates ADB backup extraction, but root access bypasses this. |
| **Evidence** | `buffer/entity/BufferedTransaction.kt` line 103: `@ColumnInfo(name = "raw_payload_json") val rawPayloadJson: String?`. `buffer/TransactionBufferManager.kt` line 287: `rawPayloadJson = rawPayloadJson` — stored without encryption. |
| **Impact** | Low: operational FCC metadata exposed in the database file. No PII or credentials. Useful only for targeted FCC hardware attacks, which require physical LAN access anyway. |
| **Recommended Fix** | Accept the current risk level given that `allowBackup="false"` is set and the data is not PII. If defense-in-depth is required, encrypt `rawPayloadJson` using `KeystoreManager` before storage, or purge it after successful cloud upload (it is redundant once the canonical fields are persisted). |

---

## AS-021: BUNDLED_PINS Fallback Is Empty — S-006 APK-Level Certificate Pinning Safety Net Non-Functional

| Field | Value |
|-------|-------|
| **ID** | AS-021 |
| **Title** | HttpCloudApiClient.BUNDLED_PINS is an empty list despite S-006 requiring APK-bundled fallback pins |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Medium |
| **Category** | Missing API Auth Headers |
| **Description** | `HttpCloudApiClient.BUNDLED_PINS` at line 627 is `emptyList()` with a TODO comment: "Replace the empty list below with real pins once TLS certificates are provisioned for the production and staging endpoints." The `buildKtorClient()` method at line 678 uses `BUNDLED_PINS` as the fallback when the `certificatePins` parameter is empty: `val effectivePins = if (certificatePins.isNotEmpty()) certificatePins else BUNDLED_PINS`. In the production DI module (`AppModule.kt` lines 110–123), bootstrap pins ARE hardcoded and passed to `create()`, so the production HTTP client does have pinning. However, `BUNDLED_PINS` serves as the compiled-in safety net per S-006: "APK-bundled fallback certificate pins ensure certificate pinning is active during device registration before SiteConfig delivers runtime pins." Because `BUNDLED_PINS` is empty: (a) Any code path that calls `HttpCloudApiClient.create(baseUrl)` without explicitly passing `certificatePins` (e.g., test helpers, future refactors, or the `updateBaseUrl()` rebuild at line 312 which passes `certificatePins` from the instance) gets zero pinning with no warning. (b) The S-006 safety net is a no-op — if the `AppModule.kt` bootstrap pin hardcoding is accidentally removed or commented out, there is no secondary defense. (c) The discrepancy between the documented security posture ("APK-bundled fallback pins") and the actual implementation (empty list) may mislead security auditors. The bootstrap pins in `AppModule.kt` (lines 110–113) should be duplicated into `BUNDLED_PINS` to provide defense-in-depth. |
| **Evidence** | `sync/CloudApiClient.kt` line 627: `val BUNDLED_PINS: List<String> = emptyList()`. Line 678: `val effectivePins = if (certificatePins.isNotEmpty()) certificatePins else BUNDLED_PINS` — fallback resolves to empty. Lines 626–627: TODO comment acknowledging pins are not set. `di/AppModule.kt` lines 110–113: bootstrap pins are hardcoded separately, not referencing `BUNDLED_PINS`. |
| **Impact** | The S-006 specification requirement for APK-level fallback pinning is not met. In the current production flow, pinning works because `AppModule.kt` passes explicit pins. The risk is in maintenance: if the `AppModule` pin list is removed or the factory is called without pins from a new code path, the connection proceeds completely unpinned with no warning. |
| **Recommended Fix** | Populate `BUNDLED_PINS` with the same SHA-256 pin hashes used in `AppModule.kt`: `val BUNDLED_PINS: List<String> = listOf("sha256/YLh1dUR9y6Kja30RrAn7JKnbQG/uEtLMkBgFF2Fuihg=", "sha256/Vjs8r4z+80wjNcr1YKepWQboSIRi63WsWXhIMN+eWys=")`. Update `AppModule.kt` to reference `HttpCloudApiClient.BUNDLED_PINS` instead of duplicating the values: `val bootstrapPins = HttpCloudApiClient.BUNDLED_PINS`. This creates a single source of truth for pin values. |

---

## AS-022: Telemetry Payload Includes FCC Host Address and Port — Potential Reconnaissance Value

| Field | Value |
|-------|-------|
| **ID** | AS-022 |
| **Title** | TelemetryPayload transmits FCC controller LAN IP and port to cloud — internal network topology exposed |
| **Module** | Cloud Sync & Telemetry |
| **Severity** | Low |
| **Category** | Sensitive Data Logged |
| **Description** | `TelemetryReporter.collectFccHealth()` at lines 223–225 includes `fccHost = cfg.fcc.hostAddress` and `fccPort = cfg.fcc.port` in the `FccHealthStatusDto`. These values are the FCC controller's LAN IP address (e.g., `192.168.1.100`) and TCP port (e.g., `5001`). The telemetry payload is transmitted over HTTPS to the cloud API. While the channel is encrypted in transit, the cloud stores these values in its telemetry database. If the cloud database is compromised (or the telemetry data is accessible to a wider audience than intended), the internal LAN topology of every site is exposed: FCC controller IP addresses and ports across all deployed sites. Combined with the `fccVendor` field (line 223), this provides a reconnaissance catalog — an attacker knows exactly which FCC devices are at which sites, their vendor type, and their LAN addresses. The `siteCode` and `deviceId` fields in the same payload further enrich the catalog. For comparison, the `@Sensitive` annotation and `SensitiveFieldFilter` are used for PII fields like `customerTaxId`, but infrastructure fields like FCC host/port have no such protection. |
| **Evidence** | `sync/TelemetryReporter.kt` lines 223–225: `fccHost = cfg.fcc.hostAddress ?: "UNCONFIGURED"`, `fccPort = cfg.fcc.port ?: 0`. `sync/CloudApiModels.kt` lines 233–234: `fccHost: String` and `fccPort: Int` in the serialized DTO. |
| **Impact** | Low: the cloud channel is HTTPS with certificate pinning, and the FCC LAN addresses are only useful from within the site's LAN. However, in a cloud breach scenario, the aggregated topology data (FCC IPs, ports, vendors, per-site) provides high-value reconnaissance for targeted attacks against fuel controller hardware. |
| **Recommended Fix** | Assess whether the cloud monitoring dashboard actually needs the exact FCC IP and port for operational purposes. If not, omit these fields from telemetry. If needed for troubleshooting, transmit only a hash or truncated value (e.g., last octet of the IP). At minimum, flag these fields as infrastructure-sensitive in the cloud-side data classification policy. |

---

## AS-023: shareLogs() Shares All Log Files Without Content Redaction — Operational Data Exposed via Share Intent

| Field | Value |
|-------|-------|
| **ID** | AS-023 |
| **Title** | DiagnosticsActivity "Share Logs" zips and shares all JSONL log files without sanitizing sensitive content |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Medium |
| **Category** | Sensitive Data Logged |
| **Description** | `DiagnosticsActivity.shareLogs()` at lines 446–492 collects all log files via `fileLogger.getLogFiles()`, zips them into `edge-agent-logs.zip`, and opens an Android Share Intent (`ACTION_SEND`) allowing the technician to send the zip via email, WhatsApp, cloud drive, or any installed sharing target. The structured log files contain: (1) FCC host address and port — logged at INFO level when config changes (e.g., `"Applied config v2 from config-update with FCC runtime vendor=DOMS host=192.168.1.100:5001"` at `EdgeAgentForegroundService.kt` line 263–267). (2) Device identity — `deviceId` and `siteCode` logged at registration success (AS-010). (3) Cloud base URL — logged when `CloudApiClient.updateBaseUrl()` is called. (4) Full exception stack traces — logged at ERROR level with class names, method names, and line numbers revealing internal architecture. (5) FCC access code may appear if `JplMessage` is serialized during error handling (AS-015). (6) Connectivity state transitions with timestamps — reveals operational uptime patterns. The `SensitiveFieldFilter` and `@Sensitive` annotation system is designed for API response filtering, NOT for log file sanitization. The `StructuredFileLogger` writes `msg` and `extra` values as-is with no filtering pass. There is no redaction step in `shareLogs()` or `getLogFiles()` before the zip is created. |
| **Evidence** | `ui/DiagnosticsActivity.kt` lines 452–468: `fileLogger.getLogFiles()` → zip → share. `service/EdgeAgentForegroundService.kt` lines 263–267: FCC host/port logged at INFO. `ui/ProvisioningViewModel.kt` line 127: deviceId/siteCode logged (AS-010). `logging/StructuredFileLogger.kt` lines 84–93: `e()` method includes full stack traces. No redaction or filtering in the shareLogs pipeline. |
| **Impact** | Technicians sharing logs for troubleshooting inadvertently expose infrastructure details (FCC IPs, cloud URLs), device identity, and code architecture to third parties. In a support workflow where logs are shared via email or messaging, these details transit and are stored on systems outside the organization's control. A malicious actor receiving these logs gains a reconnaissance advantage: FCC vendor, LAN topology, cloud endpoints, and code structure for targeted attacks. |
| **Recommended Fix** | Add a sanitization pass before zipping: filter log lines through the `SensitiveFieldFilter` or a dedicated log redaction function that replaces FCC host addresses with `[REDACTED]`, truncates deviceId, and strips stack trace details beyond the exception class and message. Alternatively, apply the redaction at write time in `StructuredFileLogger` by running the `msg` and `extra` fields through a filter before serialization. At minimum, display a warning dialog before sharing: "These logs contain device identity and network configuration. Only share with authorized support personnel." |

---

## AS-024: Structured Log Files Persist Full Stack Traces With Internal Code Structure

| Field | Value |
|-------|-------|
| **ID** | AS-024 |
| **Title** | StructuredFileLogger stores complete stack traces in persistent JSONL files — internal architecture exposed on device compromise |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | Sensitive Data Logged |
| **Description** | `StructuredFileLogger.e()` at lines 84–93 captures the full exception stack trace via `throwable.stackTraceToString().take(MAX_MESSAGE_LENGTH)` (4000 characters) and stores it in the `extra.stackTrace` field of the JSONL log entry. These log entries are persisted to `context.filesDir/logs/` as daily rolling files with up to `maxFiles=5` files (5 days of logs). The stack traces contain: fully qualified class names (`com.fccmiddleware.edge.sync.CloudUploadWorker`), method names (`uploadPendingBatch`), line numbers, and the call chain through the application. This information maps the internal architecture — package structure, class hierarchy, method signatures, and error handling paths. On a rooted or compromised device, `context.filesDir` is readable. The `shareLogs()` function (AS-023) also makes these files available off-device. While `android:allowBackup="false"` prevents ADB backup extraction, root access bypasses this. The `crash()` method at line 97 additionally logs `FATAL` level entries with full crash stack traces, which persist even after the process restarts. The 4000-character limit (`MAX_MESSAGE_LENGTH`) is generous — most stack traces are well under this limit. |
| **Evidence** | `logging/StructuredFileLogger.kt` lines 89–90: `"stackTrace" to it.stackTraceToString().take(MAX_MESSAGE_LENGTH)`. Lines 106–107: same in `crash()`. Line 50: `MAX_MESSAGE_LENGTH = 4000`. `logDir` at line 53: `context.filesDir/logs/` — app-private but root-accessible. |
| **Impact** | Low: stack traces in log files are standard practice, and the data requires device-level access (root or log sharing) to exploit. However, for a financial application controlling fuel pumps, the exposed architecture aids targeted exploit development. The risk increases when logs are shared externally (AS-023). |
| **Recommended Fix** | Accept the current risk for on-device logs — stack traces are essential for field debugging. For the `shareLogs()` path (AS-023), truncate stack traces to the exception class name and message only, stripping the call chain. Alternatively, apply R8/ProGuard obfuscation to production builds so stack traces contain obfuscated class/method names. Ensure the ProGuard mapping file is not included in the APK. |

---

## AS-025: WebSocket maxFrameSize Set to Long.MAX_VALUE — Unbounded Memory Allocation from Malicious Frames

| Field | Value |
|-------|-------|
| **ID** | AS-025 |
| **Title** | Ktor WebSocket server accepts frames up to 2^63 bytes — no practical upper bound on inbound frame size |
| **Module** | POS Integration (Odoo) |
| **Severity** | Medium |
| **Category** | Insecure Network Configuration |
| **Description** | `OdooWebSocketServer.start()` at line 134 configures the Ktor WebSocket plugin with `maxFrameSize = Long.MAX_VALUE`. This allows the server to accept a single WebSocket frame of any size, up to 9.2 exabytes (2^63 − 1 bytes). When a malicious client on the LAN sends a frame with a large length header (e.g., 1 GB), the Ktor CIO engine attempts to allocate a buffer of that size, causing `OutOfMemoryError` and crashing the entire edge agent process. The per-connection rate limiter (S-005(b)) counts messages (complete frames), not bytes — it does not protect against a single oversized frame. The `maxConnections` limit (default 10) bounds concurrent connections but not frame size. A single malicious connection can crash the server with one frame. The Ktor default `maxFrameSize` is `Long.MAX_VALUE` (per Ktor source), so this is the Ktor default being left unchanged — but the default is inappropriate for a LAN-exposed server on a memory-constrained Android device (Urovo i9100 has 2 GB RAM, ~512 MB available to apps). |
| **Evidence** | `websocket/OdooWebSocketServer.kt` line 134: `maxFrameSize = Long.MAX_VALUE`. `config/EdgeAgentConfigDto.kt` line 171: `maxConnections: Int = 10` — bounds connections, not frame size. No frame-size config parameter exists in `WebSocketDto`. |
| **Impact** | A single LAN device can crash the edge agent by sending a WebSocket frame with a large length header. On a production forecourt, this could disrupt transaction processing, FCC polling, and cloud sync until the agent restarts via START_STICKY. Repeated attacks could cause a denial-of-service loop. |
| **Recommended Fix** | Set a reasonable `maxFrameSize` limit: `maxFrameSize = 64 * 1024` (64 KB). The largest legitimate WebSocket message is a `mode: "all"` response (which the SERVER sends, not receives) or an `attendant_pump_count_update` command from the POS (typically <4 KB). A 64 KB limit provides generous headroom for legitimate messages while preventing memory exhaustion. Add a `maxFrameSizeKb` parameter to `WebSocketDto` to make it configurable. |

---

## AS-026: Default WebSocket Configuration Has No Authentication — Unauthenticated Pump Control via LAN

| Field | Value |
|-------|-------|
| **ID** | AS-026 |
| **Title** | WebSocket server defaults to no authentication and binds to all interfaces — any LAN device can query transactions and unblock pumps |
| **Module** | POS Integration (Odoo) |
| **Severity** | High |
| **Category** | Missing API Auth Headers |
| **Description** | `WebSocketDto` defaults to `sharedSecret: String? = null` (line 181), `requireApiKeyForLan: Boolean = false` (line 175), and `bindAddress: String = "0.0.0.0"` (line 169). The authentication check in `OdooWebSocketServer.start()` at lines 143–151 is: `if (!secret.isNullOrBlank()) { ... }`. When `sharedSecret` is null (the default), the `if` block is skipped entirely — ALL connections are accepted without authentication. Combined with binding to `0.0.0.0`, the WebSocket server is accessible from any network interface (WiFi, mobile data, USB tethering) without credentials. An unauthenticated attacker on the LAN can: (a) `mode: "latest"` / `mode: "all"` — extract all transaction data including amounts, pump numbers, attendant IDs, and product codes. (b) `mode: "manager_update"` — modify Odoo order references on transactions, potentially redirecting payments. (c) `mode: "fp_unblock"` — send pump release commands to the FCC, unblocking pumps that were intentionally restricted. (d) `mode: "manager_manual_update"` — mark transactions as discarded, removing them from the POS workflow. The `requireApiKeyForLan` field exists in the DTO but is NEVER checked in the server code — a search for `requireApiKeyForLan` in `OdooWebSocketServer.kt` returns zero results. This field is dead configuration. |
| **Evidence** | `config/EdgeAgentConfigDto.kt` line 181: `val sharedSecret: String? = null`. Line 175: `val requireApiKeyForLan: Boolean = false` — dead field. Line 169: `val bindAddress: String = "0.0.0.0"`. `websocket/OdooWebSocketServer.kt` lines 143–151: `if (!secret.isNullOrBlank()) { ... }` — skipped when null. Grep for `requireApiKeyForLan` in `websocket/`: 0 results. |
| **Impact** | Any device on the same network as the edge agent can connect to the WebSocket server and issue financial commands without authentication. Transaction data (including amounts and attendant IDs) is exposed. Pump release commands can be sent to the FCC. This is a direct path to financial fraud and physical asset control (fuel pumps) from an unauthenticated network position. |
| **Recommended Fix** | Change the default to require authentication: `sharedSecret` should not default to null in production builds. Add a startup warning when `sharedSecret` is null: `AppLogger.w(TAG, "SECURITY: WebSocket server running WITHOUT authentication")`. Implement the `requireApiKeyForLan` check in the server — it is documented in the DTO but never enforced. Consider binding to `127.0.0.1` by default (localhost only) and requiring explicit configuration to bind to `0.0.0.0`. At minimum, restrict sensitive commands (`fp_unblock`, `manager_update`, `manager_manual_update`) to authenticated connections even if `latest` and `all` are open. |

---

## AS-027: fp_unblock Error Response Exposes Internal Exception Messages to WebSocket Clients

| Field | Value |
|-------|-------|
| **ID** | AS-027 |
| **Title** | Pump unblock error handler sends raw exception message to POS client — leaks internal adapter details |
| **Module** | POS Integration (Odoo) |
| **Severity** | Low |
| **Category** | Sensitive Data Logged |
| **Description** | `OdooWsMessageHandler.handleFpUnblock()` at line 252 sends `e.message ?: "Unknown error"` directly in the WebSocket error response: `put("message", JsonPrimitive(e.message ?: "Unknown error"))`. Exception messages from FCC adapters contain internal details: for DOMS, `JplTcpClient` exceptions include the TCP host/port and frame parsing errors. For Radix, HTTP client exceptions include the FCC URL, HTTP status codes, and response body fragments. For Advatec, `HttpURLConnection` exceptions include the connection URL and timeout values. These details reveal FCC adapter type, protocol, LAN topology (FCC IP/port), and internal class names to the WebSocket client. In a scenario where the WebSocket server has no authentication (AS-026), any LAN device can trigger `fp_unblock` with an invalid pump number and harvest adapter-specific error messages to fingerprint the FCC deployment. |
| **Evidence** | `websocket/OdooWsMessageHandler.kt` line 252: `put("message", JsonPrimitive(e.message ?: "Unknown error"))`. Compare with `handleFuelPumpStatus` at line 198: logs the error but does NOT send `e.message` to the client. |
| **Impact** | Low: the information is primarily useful for reconnaissance (FCC vendor, LAN topology) which requires LAN access to exploit further. The adapter error messages may contain FCC host/port (also reported in AS-022 for telemetry), protocol-specific error codes, and internal class names. |
| **Recommended Fix** | Replace the raw exception message with a generic error: `put("message", JsonPrimitive("Failed to unblock pump $fpId"))`. Log the full exception with `AppLogger.w()` (which is already done at line 247). This follows the pattern used by `handleFuelPumpStatus` where the error is logged but not sent to the client. |

---

## AS-028: BoundSocketFactory Silently Falls Back to Unbound Default Routing — Cloud Traffic May Traverse Untrusted Networks

| Field | Value |
|-------|-------|
| **ID** | AS-028 |
| **Title** | BoundSocketFactory silently falls back to OS-default network routing when no bound network is available — no audit trail for cloud traffic routing |
| **Module** | Connectivity |
| **Severity** | Medium |
| **Category** | Insecure Token Storage |
| **Description** | `BoundSocketFactory.bindIfAvailable()` at lines 70–78 checks `if (network != null)` and silently skips binding when the network is null. When both mobile and WiFi networks are unavailable (or during the ~1–2s gap when `NetworkBinder.onLost` fires before `onAvailable` for a replacement network), `networkProvider()` returns null. In this state, all cloud-bound sockets — including those carrying bearer tokens in Authorization headers, transaction upload payloads with financial data, and telemetry reports — are created without network binding and use whatever route the Android OS selects. On a device with multiple network interfaces (WiFi, mobile, USB tethering, WiFi Direct), the OS may route unbound sockets over an unexpected interface. In a fuel station environment, this could mean cloud traffic (with bearer tokens) routes over the station's customer WiFi if it's the only available network, potentially exposing it to MITM if the customer WiFi is open or compromised. The certificate pinning on `HttpCloudApiClient` mitigates MITM for cloud API calls (server identity is verified), but the `probeHttpClient` used for internet probes (AppModule line 174) does NOT have certificate pinning — it accepts any valid TLS certificate. A probe HTTP GET to `/health` over an attacker-controlled network could be intercepted to inject a `200 OK` response, making `ConnectivityManager` report `FULLY_ONLINE` when the actual cloud endpoint is unreachable. No log entry is emitted when `bindIfAvailable` skips binding. The only indication is the debug-level `logProbeNetwork` in `ConnectivityManager`, which shows "no bound network" but is easily lost in log volume. |
| **Evidence** | `connectivity/BoundSocketFactory.kt` lines 70–78: `private fun bindIfAvailable(socket: Socket) { val network = networkProvider(); if (network != null) { network.bindSocket(socket) } }` — no logging when network is null. `di/AppModule.kt` lines 174–178: `probeHttpClient` has no certificate pinning. Lines 129: `BoundSocketFactory { networkBinder.cloudNetwork.value }` — cloud API client uses same factory. |
| **Impact** | When both mobile and WiFi networks are transiently unavailable (e.g., during roaming, airplane mode toggle, or NetworkBinder race condition from AF-049), cloud traffic temporarily routes over OS-default routing. If the device has access to an untrusted network, bearer tokens and financial data may traverse it. The pinned cloud API client is protected against MITM, but the unpinned probe client is vulnerable to response injection. |
| **Recommended Fix** | Add a WARN-level log when `bindIfAvailable` skips binding: `AppLogger.w(TAG, "No bound network available — socket using default OS routing")`. Add certificate pinning to the `probeHttpClient` in AppModule (use the same bootstrap pins as the cloud API client). Consider failing the probe when no bound network is available (return false) rather than probing over an uncontrolled route — this ensures `ConnectivityState` accurately reflects reachability over the correct network. |

---

## AS-029: Internet Probe OkHttpClient Lacks Certificate Pinning — Probe Result Can Be Spoofed via MITM

| Field | Value |
|-------|-------|
| **ID** | AS-029 |
| **Title** | The internet connectivity probe uses a separate OkHttpClient without certificate pinning — attacker on the network path can inject a 200 OK to spoof connectivity |
| **Module** | Connectivity |
| **Severity** | Medium |
| **Category** | Missing API Auth Headers |
| **Description** | `AppModule.kt` creates a `probeHttpClient` at lines 174–178 for the internet connectivity probe. This client uses `BoundSocketFactory` for network binding but does NOT configure a `CertificatePinner`. It makes an HTTP GET to `${cloudBaseUrl}/health` and checks `it.isSuccessful` (line 191). In contrast, the `HttpCloudApiClient` (created at lines 103–131) uses `CertificatePinner` with bootstrap or runtime pins (lines 110–123) — all cloud API traffic is pinned. A network-level attacker (e.g., rogue AP, ARP spoofing on station LAN, compromised upstream router) who can intercept the probe's TCP connection can: (1) present their own TLS certificate (accepted by the probe because there's no pinning — only standard CA validation), and (2) return `200 OK` to the `/health` request. This makes `ConnectivityManager` transition to `internetUp = true` and eventually `FULLY_ONLINE`. The `CadenceController` then triggers cloud upload, config poll, and telemetry — all of which go through the PINNED `HttpCloudApiClient` and fail with a certificate pinning violation. The practical effect is: the agent spends resources attempting uploads that fail, generates error logs, and increments error counters. The probed "FULLY_ONLINE" state is incorrect because the actual cloud endpoint is not reachable (only the MITM is). The CadenceController cannot distinguish "internet up but cloud pinning failed" from "cloud server has a misconfigured certificate" — both produce the same symptoms. |
| **Evidence** | `di/AppModule.kt` lines 174–178: `OkHttpClient.Builder().socketFactory(...).connectTimeout(4, ...).readTimeout(4, ...).build()` — no `certificatePinner()`. Compare lines 682–699 of `sync/CloudApiClient.kt`: `CertificatePinner.Builder()` with pin hashes. Line 191: `probeHttpClient.newCall(request).execute().use { it.isSuccessful }` — accepts any valid TLS response. |
| **Impact** | An attacker with network interception capability can make the agent believe cloud connectivity is available when it is not, causing wasted upload attempts, misleading telemetry (reports FULLY_ONLINE to cloud), and incorrect connectivity state on the DiagnosticsActivity screen. The pinned CloudApiClient prevents actual data exfiltration. |
| **Recommended Fix** | Add the same certificate pinner to `probeHttpClient` that `HttpCloudApiClient` uses. Extract the `CertificatePinner` construction into a shared utility function and apply it to both clients. This ensures the probe result is only positive when the genuine cloud endpoint (with the expected certificate chain) is reachable. |

---

## AS-030: SensitiveFieldFilter Not Wired Into Production Logging — @Sensitive Annotations Have No Runtime Effect

| Field | Value |
|-------|-------|
| **ID** | AS-030 |
| **Title** | SensitiveFieldFilter is built and tested but never called from production code — 10 @Sensitive-annotated fields have no runtime log redaction |
| **Module** | Security |
| **Severity** | High |
| **Category** | Sensitive Data Logged |
| **Description** | The Security module provides a complete PII/credential redaction system: (1) `@Sensitive` annotation (`Sensitive.kt`) with `RUNTIME` retention, (2) `SensitiveFieldFilter` reflection-based redactor (`SensitiveFieldFilter.kt`) that replaces `@Sensitive` field values with `[REDACTED]` or `...last8chars` for JWTs, (3) 13 test methods in `SecurityHardeningTest.kt` validating redaction behavior. The `@Sensitive` annotation is applied to 10 fields across two production model files: `CloudApiModels.kt` (`provisioningToken`, `deviceToken`, `refreshToken` on 4 DTOs) and `AdapterTypes.kt` (`customerTaxId`, `authCredential`, `sharedSecret`, `fcAccessCode`, `clientId`, `clientSecret`, `webhookSecret`, `advatecWebhookToken`). However, a search for `SensitiveFieldFilter.redact` or `SensitiveFieldFilter.redactToString` in ALL production Kotlin files (`src/main/kotlin/`) returns ZERO results. The filter is only used in test code (`SecurityHardeningTest.kt`). The production logging pipeline — `AppLogger.i()`, `AppLogger.w()`, `AppLogger.e()` backed by `StructuredFileLogger` — performs no sensitive field filtering. All log messages use raw string interpolation (e.g., `AppLogger.i(TAG, "Registration successful: deviceId=${response.deviceId}")` at `ProvisioningViewModel.kt` line 127). If any production code path ever logs a `@Sensitive`-annotated object using `toString()` (standard Kotlin data class toString includes all fields), the sensitive value appears in plaintext in the persistent structured log files and is shareable via `DiagnosticsActivity.shareLogs()` (AS-023). `SecurityHardeningTest` line 621 explicitly PROVES this risk: `"Raw toString() leaks sensitive data — use SensitiveFieldFilter.redactToString() instead"`. This is the FOURTH instance of the "built, tested, but never wired" pattern in the codebase, following CleanupWorker (AF-034), IntegrityChecker (AF-038), and KeystoreManager.rotateKey (AT-051). |
| **Evidence** | `security/SensitiveFieldFilter.kt`: complete implementation. `security/Sensitive.kt`: `@Retention(RUNTIME)` annotation. Grep for `SensitiveFieldFilter` in `src/main/kotlin/`: only `SensitiveFieldFilter.kt` itself (definition) and `Sensitive.kt` (doc reference). Grep for `SensitiveFieldFilter` in `src/test/kotlin/`: `SecurityHardeningTest.kt` — 13 test methods. `sync/CloudApiModels.kt`: `@Sensitive val provisioningToken`, `@Sensitive val deviceToken`, `@Sensitive val refreshToken` (4 DTOs). `adapter/common/AdapterTypes.kt`: `@Sensitive val customerTaxId`, `@Sensitive val authCredential`, etc. (8 fields). `security/SecurityHardeningTest.kt` line 621: test proving `toString()` leaks sensitive data. |
| **Impact** | The `@Sensitive` annotation gives developers and security auditors a false sense of security — fields appear to be protected for log redaction, but the redaction mechanism is never invoked. If any current or future code path logs a `TokenRefreshRequest`, `DeviceRegistrationResponse`, `AgentFccConfig`, or `PreAuthCommand` object (directly or via exception serialization), sensitive credentials and PII appear in plaintext in persistent JSONL log files. These log files can be shared externally via the diagnostics screen (AS-023). The provisioning token, refresh token, FCC access codes, customer tax IDs, and OAuth client secrets are all at risk. |
| **Recommended Fix** | Wire `SensitiveFieldFilter` into the `StructuredFileLogger` write path. Add a `sanitize()` step in `StructuredFileLogger.writeEntry()` that runs the `msg` and `extra` fields through a pattern-based sanitizer (checking for known sensitive field names like "token", "secret", "credential", "taxId"). For structured logging of objects, add an `AppLogger.redacted(tag, obj)` method that calls `SensitiveFieldFilter.redactToString(obj)` and passes the result to `writeEntry()`. Additionally, override `toString()` on all `@Sensitive`-annotated data classes to use `SensitiveFieldFilter.redactToString(this)` — this ensures that even accidental `toString()` calls in log interpolation are redacted. |

---

## AS-031: Token Blob Writes Use apply() Instead of commit() — Tokens Lost on Process Death During Refresh

| Field | Value |
|-------|-------|
| **ID** | AS-031 |
| **Title** | storeDeviceTokenBlob and storeRefreshTokenBlob use async apply() — token state not crash-safe during refresh |
| **Module** | Security |
| **Severity** | Medium |
| **Category** | Insecure Token Storage |
| **Description** | `EncryptedPrefsManager.storeDeviceTokenBlob()` (line 135) and `storeRefreshTokenBlob()` (line 143) both use `prefs.edit().putString(...).apply()`. The `apply()` method writes to the in-memory SharedPreferences map synchronously but defers disk persistence to a background thread. The same class uses `commit()` (synchronous disk write) for `isDecommissioned` (line 102), `isReprovisioningRequired` (line 117), `setReprovisioningAndUnregister` (line 129), and `saveRegistration` (line 163-172), with explicit comments explaining crash-safety requirements. During token refresh (`KeystoreDeviceTokenProvider.refreshAccessToken()` line 113), the cloud issues a new device token and new refresh token. The old refresh token is single-use and is now invalidated server-side. `storeTokens()` writes the new tokens via `storeDeviceTokenBlob()` and `storeRefreshTokenBlob()` — both using `apply()`. If the process is killed between the `apply()` call and the background disk flush (a window of 100–500ms on slow storage): (a) the old token blobs survive on disk, (b) on restart, `getAccessToken()` returns the old device token (expired), (c) `refreshAccessToken()` uses the old refresh token, which the cloud rejects (already consumed), (d) the device enters re-provisioning state. This failure mode is identical to AF-012 (`clearAll()` using `apply()` instead of `commit()`). The inconsistency is notable: the class explicitly documents crash-safety for boolean flags but uses the crash-unsafe `apply()` for the security-critical token blobs. |
| **Evidence** | `security/EncryptedPrefsManager.kt` line 135: `prefs.edit().putString(KEY_DEVICE_TOKEN_ENCRYPTED, encoded).apply()`. Line 143: `prefs.edit().putString(KEY_REFRESH_TOKEN_ENCRYPTED, encoded).apply()`. Compare with line 102: `prefs.edit().putBoolean(KEY_IS_DECOMMISSIONED, value).commit()` — explicit crash-safety comment. Lines 163–172: `saveRegistration()` uses `.commit()`. |
| **Impact** | On process death during token refresh (which occurs every 24 hours under normal operation), the device loses the new tokens. The old refresh token is rejected by the cloud (single-use, already consumed). The device enters unnecessary re-provisioning, requiring a new bootstrap token from the portal — a manual IT process. With 100 deployed devices refreshing daily, even a 0.1% process death rate during the 100–500ms apply window causes 3–4 unnecessary re-provisioning events per month across the fleet. |
| **Recommended Fix** | Change both methods to use `commit()`: `fun storeDeviceTokenBlob(encoded: String) { prefs.edit().putString(KEY_DEVICE_TOKEN_ENCRYPTED, encoded).commit() }` and `fun storeRefreshTokenBlob(encoded: String) { prefs.edit().putString(KEY_REFRESH_TOKEN_ENCRYPTED, encoded).commit() }`. Better yet, batch both writes in a single commit: add a `storeTokenBlobs(deviceBlob: String, refreshBlob: String)` method that writes both in one `prefs.edit()...commit()`, matching the atomic pattern of `saveRegistration()`. |

---

## AS-032: KeystoreManager Does Not Verify Hardware-Backed Key Storage — No TEE Confirmation or Warning

| Field | Value |
|-------|-------|
| **ID** | AS-032 |
| **Title** | KeystoreManager creates keys without verifying hardware backing and does not log a warning when keys are software-backed |
| **Module** | Security |
| **Severity** | Low |
| **Category** | Insecure Token Storage |
| **Description** | `KeystoreManager.getOrCreateKey()` at lines 159–176 creates AES-256-GCM keys with `setUserAuthenticationRequired(false)` but does not call `setIsStrongBoxBacked(true)` (API 28+) or verify that the generated key is hardware-backed. After key generation, `android.security.keystore.KeyInfo.isInsideSecureHardware()` is never checked. The class doc (line 16) states "hardware TEE where available (Urovo i9100)" — implying hardware backing is expected but not enforced. On devices without hardware-backed Keystore (emulators used for testing, some budget Android devices that may be substituted in field deployments, devices where the TEE is in a degraded state), keys are stored in a software-backed Keystore. Software-backed keys can be extracted by a root user by reading the `/data/misc/keystore/` directory, defeating the security guarantees that the rest of the codebase assumes. The `SecurityHardeningTest` validates key alias names and annotation placements but does not test for hardware backing — there is no integration test that calls `KeyInfo.isInsideSecureHardware()` on the generated key. |
| **Evidence** | `security/KeystoreManager.kt` lines 163–171: `KeyGenParameterSpec.Builder(...)` — no `.setIsStrongBoxBacked(true)`. Line 16: "hardware TEE where available (Urovo i9100)." No call to `SecretKeyFactory.getInstance("AES", "AndroidKeyStore").getKeySpec(key, KeyInfo::class.java).isInsideSecureHardware` anywhere in the class. |
| **Impact** | On devices without hardware-backed Keystore, all secrets (device JWT, refresh token, FCC credentials, LAN API key, config integrity key) are stored in software-backed encryption. A root user can extract the Keystore blobs and decrypt them. Low severity because: (1) the target device (Urovo i9100) supports hardware-backed Keystore, (2) rooting a production device requires physical access. |
| **Recommended Fix** | After key generation in `getOrCreateKey()`, check hardware backing: `val keyInfo = SecretKeyFactory.getInstance("AES", "AndroidKeyStore").getKeySpec(key, KeyInfo::class.java) as KeyInfo; if (!keyInfo.isInsideSecureHardware) { AppLogger.w(TAG, "SECURITY: Key alias=$alias is SOFTWARE-backed — not protected by hardware TEE") }`. This provides an audit trail when keys are not hardware-backed. Optionally, add a telemetry field `keystoreHardwareBacked: Boolean` to the `DeviceStatusDto` so the cloud monitoring dashboard can detect devices with degraded key protection.

---

## AS-033: No FLAG_SECURE on Sensitive Activities — Screenshots and Task Switcher Expose Credentials

| Field | Value |
|-------|-------|
| **ID** | AS-033 |
| **Title** | ProvisioningActivity and SettingsActivity do not set FLAG_SECURE — sensitive data visible in screenshots and recent apps |
| **Module** | UI / Security |
| **Severity** | Medium |
| **Category** | Sensitive Data Exposure |
| **Description** | Neither `ProvisioningActivity` nor `SettingsActivity` set `window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)`. `ProvisioningActivity` displays the bootstrap provisioning token during QR code scanning and manual entry (lines 98–125). `SettingsActivity` displays and accepts the FCC access code — a credential that controls physical fuel pump operations. Without `FLAG_SECURE`: (1) Screenshots capture the provisioning token or FCC access code in full. (2) The Android task switcher (recent apps) shows a live thumbnail of the activity content, including any visible credentials. (3) Screen recording apps and screen-sharing tools (including ADB `screenrecord`) capture the content. (4) On Android 12+, the Pixel screenshot editor and Google Lens can OCR the text from the thumbnail. The `DiagnosticsActivity` also displays structured log entries and audit trails that may contain operational-sensitive data (FCC host addresses per AS-022, device identity per AS-010), though this is lower severity since the data is not directly credential-level. The `DecommissionedActivity` does not display sensitive data and does not need FLAG_SECURE. |
| **Evidence** | `ui/ProvisioningActivity.kt`: no `FLAG_SECURE` in `onCreate()`. `ui/SettingsActivity.kt`: no `FLAG_SECURE` in `onCreate()`. Android `FLAG_SECURE` documentation: "Treats the content of the window as secure, preventing it from appearing in screenshots or from being viewed on non-secure displays." |
| **Impact** | A technician taking a screenshot during provisioning inadvertently captures the bootstrap token. If the screenshot is shared (e.g., in a support chat or photo backup), the token can be used to register a rogue device. FCC access codes captured in screenshots or task switcher thumbnails could be used by unauthorized personnel to control fuel pumps. On devices with cloud photo backup enabled, screenshots auto-sync to external services. |
| **Recommended Fix** | Add `FLAG_SECURE` to `ProvisioningActivity` and `SettingsActivity` in `onCreate()` before `setContentView()`: `window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)`. Optionally add it to `DiagnosticsActivity` as well. Do NOT add to `SplashActivity`, `LauncherActivity`, or `DecommissionedActivity` — these do not display sensitive data. |

---

## AS-034: android.util.Log Calls Not Stripped in Release Builds — Debug Output Accessible via ADB Logcat

| Field | Value |
|-------|-------|
| **ID** | AS-034 |
| **Title** | ProGuard/R8 rules do not strip android.util.Log calls — all log levels accessible via adb logcat in release APK |
| **Module** | Security / Build Configuration |
| **Severity** | Medium |
| **Category** | Sensitive Data Logged |
| **Description** | The `proguard-rules.pro` file contains rules for keeping kotlinx.serialization, Ktor, Room, Koin, and coroutines classes but does NOT include the standard rule for stripping `android.util.Log` calls in release builds. The `AppLogger` facade (lines 8–12 of `AppLogger.kt`) delegates to `StructuredFileLogger` when initialized but falls back to `android.util.Log` when the delegate is not yet set (early app startup). Even after initialization, `StructuredFileLogger` writes to the structured JSONL file but the `android.util.Log` fallback path still exists. Additionally, the `LogLevel` comments (line 15) note: "Messages below configured level are dropped from file output (still forwarded to android.util.Log for ADB debugging)." This confirms that even when the file logger's level is set to WARN, DEBUG and INFO messages still reach `android.util.Log`. Any device connected via USB or wireless ADB exposes the full logcat stream. Logcat output includes: (a) tag names revealing internal class structure (e.g., `CloudUploadWorker`, `PreAuthHandler`, `KeystoreManager`), (b) operational messages with transaction IDs, pump numbers, site codes, (c) error messages with stack trace fragments, (d) connectivity state transitions and timing. On production Urovo i9100 devices in the field, USB debugging may be enabled for maintenance purposes. Station technicians or any person with physical access to the USB port can capture logcat output with `adb logcat`. |
| **Evidence** | `app/proguard-rules.pro`: no `-assumenosideeffects class android.util.Log` rule. `logging/LogLevel.kt` line 15: "still forwarded to android.util.Log for ADB debugging." `logging/AppLogger.kt` lines 8–12: fallback to `android.util.Log`. `app/build.gradle.kts` lines 24–29: `isMinifyEnabled = true` but no log stripping. |
| **Impact** | All log output — including DEBUG level — is accessible via `adb logcat` on any device with USB debugging enabled. This includes operational data (transaction IDs, pump numbers, site codes), connectivity state, and error details. While no tokens or credentials are logged directly (per the `@Sensitive` annotation policy and AS-030's analysis), the operational metadata provides reconnaissance value. On devices where USB debugging is enabled for field maintenance, any person with a USB cable and laptop can passively capture the log stream. |
| **Recommended Fix** | Add standard log stripping rules to `proguard-rules.pro` for release builds: `-assumenosideeffects class android.util.Log { public static int d(...); public static int v(...); }`. This strips DEBUG and VERBOSE calls at compile time. Keep INFO, WARN, and ERROR for production diagnostics. Alternatively, guard the `android.util.Log` fallback in `AppLogger` with `if (BuildConfig.DEBUG)` so only the file-based structured logger operates in release builds. Additionally, consider disabling USB debugging on production devices via device management policy (MDM). |

---

## AS-035: Room Database Not Encrypted — All Buffered Data Accessible on Rooted Devices

| Field | Value |
|-------|-------|
| **ID** | AS-035 |
| **Title** | fcc_buffer.db uses standard unencrypted SQLite — no SQLCipher or equivalent database-at-rest encryption |
| **Module** | Buffer / Security |
| **Severity** | Medium |
| **Category** | Insecure Data Storage |
| **Description** | `BufferDatabase.create()` (line 192–205 of `BufferDatabase.kt`) builds the Room database with `Room.databaseBuilder()` using standard SQLite with WAL journaling. Neither SQLCipher (`net.zetetic:android-database-sqlcipher`) nor any other encryption layer is applied. The database file `fcc_buffer.db` resides in the app's private directory (`/data/data/com.fccmiddleware.edge/databases/`) and is protected by Android's file-level permissions (mode 660, accessible only to the app's UID). However, on rooted devices or via physical extraction (JTAG, chip-off), the database is readable as a standard SQLite3 file. The database contains 9 tables across schema version 6: (1) `buffered_transactions` — transaction amounts, product codes, pump numbers, attendant IDs, raw FCC payloads, (2) `pre_auth_records` — customer tax IDs (PII, per AS-008), authorization codes, pump assignments, (3) `audit_log` — timestamped operational audit trail with event types and messages, (4) `agent_config` — site configuration including FCC connection parameters, (5) `site_info`, `local_products`, `local_pumps`, `local_nozzles` — site topology and product catalog, (6) `sync_state` — cloud synchronization metadata, (7) `nozzles` — nozzle-to-product mapping. The `android:allowBackup="false"` manifest flag prevents ADB backup extraction on non-rooted devices, but does not protect against root-level access. Individual findings AS-008 (customerTaxId) and AS-017 (rawPayloadJson) address specific sensitive columns; this finding addresses the architectural decision to use unencrypted SQLite for the entire database. The `build.gradle.kts` dependencies (lines 61–115) do not include SQLCipher or any database encryption library. |
| **Evidence** | `buffer/BufferDatabase.kt` lines 192–205: `Room.databaseBuilder()` with no encryption. `app/build.gradle.kts`: no SQLCipher dependency. Schema file `schemas/.../6.json`: 9 tables with sensitive operational and PII data. `AndroidManifest.xml` line 18: `android:allowBackup="false"`. |
| **Impact** | On rooted or physically compromised devices, the entire transaction history, pre-auth records (including customer tax IDs), site configuration, and operational audit trail are accessible as a standard SQLite file. In a fuel station environment, devices may be physically accessible to station staff or maintenance personnel. The Urovo i9100 running a custom firmware could potentially be rooted. Combined with the data retention period (configurable via SiteConfig, potentially days or weeks of transaction history), a single device compromise exposes significant financial and PII data. |
| **Recommended Fix** | Integrate SQLCipher via `net.zetetic:android-database-sqlcipher:4.5.6` and `androidx.sqlite:sqlite-ktx`. Modify `BufferDatabase.create()` to use `SupportFactory` with a key derived from the Android Keystore: `val passphrase = keystoreManager.getOrCreateDatabaseKey()`. This provides defense-in-depth: even on rooted devices, the database is encrypted with a hardware-backed key. If SQLCipher's performance overhead (typically <5%) is a concern on the Urovo i9100, encrypt only the `pre_auth_records` table's `customerTaxId` column at the application layer (per AS-008's recommendation) and accept the risk for non-PII operational data. |

---

## AS-036: Exception Messages Logged Without Redaction — Sensitive Data May Leak via Throwable.message

| Field | Value |
|-------|-------|
| **ID** | AS-036 |
| **Title** | Global and coroutine exception handlers log throwable.message without filtering — sensitive values in exception messages written to persistent logs |
| **Module** | Security / Logging |
| **Severity** | Medium |
| **Category** | Sensitive Data Logged |
| **Description** | The application has two global exception handlers that log `throwable.message` directly: (1) `FccEdgeApplication.onCreate()` sets `Thread.setDefaultUncaughtExceptionHandler` which calls `logger.crash(tag, "Uncaught exception on thread ${thread.name}: ${throwable.message}", throwable)` (lines 27–41). (2) The `CoroutineExceptionHandler` in `AppModule.kt` calls `logger.e("CoroutineScope", "Uncaught coroutine exception: ${throwable.message}", throwable)`. Neither handler runs `throwable.message` through `SensitiveFieldFilter` or any redaction pass. Exception messages from Android and third-party libraries can contain sensitive data: (a) `java.net.ConnectException`: includes the target host and port (e.g., "Failed to connect to 192.168.1.100:5001"), exposing FCC LAN topology. (b) `javax.net.ssl.SSLHandshakeException`: may include certificate details, hostname, and pin violations. (c) `kotlinx.serialization.SerializationException`: includes the raw JSON fragment that failed to parse — if the JSON contains `@Sensitive` fields (tokens, credentials), they appear in the error message. (d) `java.lang.IllegalArgumentException`: may include the argument value that caused the error (e.g., an invalid token string). (e) Room `SQLiteConstraintException`: includes the SQL statement with bound parameter values. These messages are written to the persistent JSONL log files by `StructuredFileLogger`, retained for up to 5 days, and shareable via `DiagnosticsActivity.shareLogs()` (AS-023). The `SensitiveFieldFilter` (AS-030) is designed to redact `@Sensitive` fields on data class instances, not on arbitrary string messages — even if it were wired into the logging pipeline, it would not catch sensitive data embedded in exception messages. |
| **Evidence** | `FccEdgeApplication.kt` lines 27–41: `throwable.message` logged directly. `di/AppModule.kt`: `CoroutineExceptionHandler { _, throwable -> logger.e("CoroutineScope", "Uncaught coroutine exception: ${throwable.message}", throwable) }`. `logging/StructuredFileLogger.kt` lines 84–93: `e()` stores `msg` and `throwable.stackTraceToString()` in JSONL. No redaction pass on `msg` before serialization. |
| **Impact** | Sensitive data embedded in exception messages (FCC host/port, raw JSON fragments with tokens, SQL statements) is persisted to log files and potentially shared externally. The risk is probabilistic — it depends on whether exceptions are thrown from code paths processing sensitive data. Given that the application handles tokens, credentials, and PII as part of its normal operation, the probability is non-trivial over the device's lifetime. |
| **Recommended Fix** | Add a message sanitizer to `StructuredFileLogger` that runs all log messages through a pattern-based filter before writing. The filter should: (1) replace IP:port patterns with `[host:port]` unless in DEBUG mode, (2) replace JSON fragments containing known sensitive keys ("token", "secret", "credential", "password", "taxId") with `[REDACTED_JSON]`, (3) truncate raw SQL statements to the table and operation only (e.g., "INSERT INTO pre_auth_records..."). Implement as a `LogSanitizer` interface injected into `StructuredFileLogger` at construction time, allowing different sanitization levels for debug vs. release builds.

---

## AS-037: Diagnostic ZIP File Not Explicitly Deleted After Sharing — Credentials May Persist in Cache

| Field | Value |
|-------|-------|
| **ID** | AS-037 |
| **Title** | DiagnosticsActivity creates a zip file in cacheDir for sharing but does not delete it after the share intent completes |
| **Module** | Diagnostics & Monitoring |
| **Severity** | Low |
| **Category** | Insecure File Handling |
| **Description** | `DiagnosticsActivity.shareLogs()` (lines 389–404) creates a zip file in `cacheDir` containing all structured log files (JSONL), then opens a `FileProvider`-backed share intent with `Intent.ACTION_SEND`. After `startActivity(Intent.createChooser(...))`, no cleanup code deletes the zip file. The `cacheDir` is app-private (`/data/data/com.fccmiddleware.edge/cache/`) and Android may garbage-collect it when storage is low, but there is no deterministic deletion. The zip file persists indefinitely on devices with sufficient storage. On the memory-constrained Urovo i9100 (typically ~4 GB usable storage), multiple share operations create multiple zip files. Each zip contains up to 5 days of structured logs (per the rolling file policy) which may include operational data, error details, and any sensitive data that reached the log pipeline (per AS-023, AS-024, AS-030, AS-036). On rooted devices, the cache directory is readable. The `android:allowBackup="false"` flag prevents ADB backup extraction of cache files on non-rooted devices. |
| **Evidence** | `ui/DiagnosticsActivity.kt` line 389: `val zipFile = File(cacheDir, "edge-agent-logs.zip")`. Lines 400–404: `FileProvider.getUriForFile(...)` followed by `startActivity(Intent.createChooser(...))`. No `zipFile.delete()` or `registerForActivityResult` callback to clean up. |
| **Impact** | Low: the zip file is in app-private cache and contains the same data as the source log files (which also persist). The additional risk is that the zip aggregates all log files into a single extractable artifact, and multiple share operations accumulate copies. |
| **Recommended Fix** | Register an `ActivityResultLauncher` for the share intent and delete the zip file in the result callback: `shareLauncher.launch(Intent.createChooser(...))` with `onResult { zipFile.delete() }`. Alternatively, use a fixed filename (`edge-agent-logs.zip`) so subsequent shares overwrite the previous zip, and delete old zips in `onResume()`. At minimum, add a `cacheDir.listFiles()?.filter { it.name.startsWith("edge-agent-logs") }?.forEach { it.delete() }` cleanup in `onCreate()`. |
