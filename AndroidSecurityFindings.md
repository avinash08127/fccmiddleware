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
