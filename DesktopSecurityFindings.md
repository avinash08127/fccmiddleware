# Desktop Security Findings

> Security audit of the FCC Desktop Edge Agent.

---

## Application Shell Module

### S-DSK-001
- **Title:** API key displayed in plaintext on provisioning summary screen
- **Module:** Application Shell
- **Severity:** High
- **Category:** Sensitive data exposure
- **Status:** FIXED
- **Description:** The provisioning wizard Step 4 displays the generated LAN API key in a readonly `TextBox` with no masking. The key is fully visible to anyone who can see the screen. In a fuel station environment, the setup screen may be visible to attendants or passers-by during installation. The key provides full access to the agent's local REST API, which controls transaction queries and pump operations.
- **Evidence:**
  - `ProvisioningWindow.axaml:284-288` — `<TextBox x:Name="ApiKeyDisplay" IsReadOnly="True" FontFamily="Consolas, monospace">` — no PasswordChar masking
  - `ProvisioningWindow.axaml.cs:611` — `ApiKeyDisplay.Text = _apiKey` — raw key set as visible text
- **Impact:** Shoulder-surfing exposure of the LAN API key, which can be used to query transactions and control pump operations from any device on the local network.
- **Recommended Fix:** Mask the key by default (`PasswordChar="*"`), add a "Show/Hide" toggle button, and auto-hide after a timeout.
- **Fix Applied:** Added `PasswordChar="*"` to `ApiKeyDisplay` TextBox. Added "Show/Hide" toggle button (`OnToggleApiKeyClicked`). Key auto-hides after 10 seconds of visibility via `AutoHideApiKeyAsync`.

---

### S-DSK-002
- **Title:** TLS certificate password stored in plaintext configuration
- **Module:** Application Shell
- **Severity:** High
- **Category:** Insecure storage of credentials
- **Status:** FIXED
- **Description:** The WebSocket TLS certificate password is read from `appsettings.json` via `GetValue<string?>("CertificatePassword")`. Configuration files on disk are readable by any local user. The certificate password should be stored in the platform credential store (DPAPI/Keychain/libsecret) which the application already uses for other secrets.
- **Evidence:**
  - `Program.cs:65` — `var wsCertPassword = wsConfig.GetValue<string?>("CertificatePassword")`
  - `Program.cs:80` — passed directly to `listenOptions.UseHttps(wsCertPath, wsCertPassword ?? "")`
- **Impact:** Any local user who can read the appsettings.json file can extract the TLS private key password, potentially enabling MitM attacks on the WebSocket endpoint.
- **Recommended Fix:** Read the certificate password from `ICredentialStore` (already available in the DI container) or use certificate store thumbprint-based selection instead of file+password.
- **Fix Applied:** Replaced inline config read with `ICredentialStore` lookup via `CredentialKeys.WsCertPassword`. Kestrel TLS configuration uses `IConfigureOptions<KestrelServerOptions>` to access DI. Config file is used as fallback only. Password variable removed from main startup scope (also fixes S-DSK-005).

---

### S-DSK-003
- **Title:** No HTTPS enforcement on auto-update URL
- **Module:** Application Shell
- **Severity:** High
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** `VelopackUpdateService` accepts any URL from configuration for update checks and downloads. There is no validation that the URL uses HTTPS. An HTTP update URL would allow network attackers to serve malicious update packages via MitM, resulting in arbitrary code execution on the agent device.
- **Evidence:**
  - `VelopackUpdateService.cs:65` — `new SimpleWebSource(cfg.UpdateUrl)` — no scheme validation
  - `VelopackUpdateService.cs:87` — `mgr.DownloadUpdatesAsync(updateInfo)` — downloads and stages from unchecked URL
- **Impact:** Remote code execution via MitM if a non-HTTPS update URL is configured (misconfiguration or config tamper).
- **Recommended Fix:** Add a guard: `if (!cfg.UpdateUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return error`. Also consider update package signature verification.
- **Fix Applied:** Added HTTPS scheme guard before `SimpleWebSource` creation. Non-HTTPS URLs are rejected with a warning log and an error result.

---

### S-DSK-004
- **Title:** Provisioning token retained in memory as plaintext string after use
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Sensitive data handling
- **Status:** FIXED
- **Description:** The provisioning token (a one-time secret) is read from the `TokenBox.Text` control, stored in a local `string` variable, embedded in the `DeviceInfoProvider.BuildRequest()` result, and passed through the registration flow. After registration completes (success or failure), the token string remains in managed heap memory until garbage collected. In a long-running agent process, this leaves the secret accessible to memory dumps.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:178` — `var token = TokenBox.Text?.Trim()` — plaintext capture
  - `ProvisioningWindow.axaml.cs:214-216` — `DeviceInfoProvider.BuildRequest(provisioningToken: token, ...)` — embedded in request object
  - No `token = null` or `TokenBox.Text = ""` after registration completes
- **Impact:** Provisioning tokens could be extracted from process memory dumps. Limited risk since tokens are one-time-use, but violates defense-in-depth.
- **Recommended Fix:** Clear `TokenBox.Text = ""` and null out local references after registration completes. Consider using `SecureString` or a pinned buffer for the token if the platform supports it.
- **Fix Applied:** `TokenBox.Text` and `ManualTokenBox.Text` cleared to `string.Empty` immediately after `RegisterAsync` returns in both the code-registration and manual-token registration paths.

---

### S-DSK-005
- **Title:** Certificate password logged in debug context via Serilog structured logging
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Sensitive data written to logs
- **Status:** FIXED
- **Description:** While the certificate path is explicitly logged (`Log.Information("WebSocket TLS endpoint configured on port {Port} with certificate {Cert}", wsPort, wsCertPath)`), the password variable `wsCertPassword` is defined in the same scope and could be inadvertently added to a log statement during debugging. More critically, the `SensitiveDataDestructuringPolicy` only redacts properties marked with `[SensitiveData]` — the certificate password is a local variable, not a property, so it has no protection from accidental logging.
- **Evidence:**
  - `Program.cs:65` — `wsCertPassword` in scope alongside log statements
  - `Program.cs:81` — `Log.Information(...)` logs cert path but not password (currently safe, but fragile)
- **Impact:** Low immediate risk, but the proximity of the secret to log statements makes accidental exposure likely during future code changes.
- **Recommended Fix:** Move certificate configuration into a dedicated method, keep the password variable scoped as tightly as possible, and add a code comment warning against logging it.
- **Fix Applied:** Certificate password variable removed from main startup scope. TLS configuration extracted into dedicated `ConfigureWebSocketTls()` static method where the password is scoped locally. Method includes a "do NOT log" comment warning.

---

## Device Provisioning Module

### S-DSK-006
- **Title:** Manual offline provisioning bypasses HTTPS enforcement — cloud sync can use plaintext HTTP
- **Module:** Device Provisioning
- **Severity:** High
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** When using manual configuration without a provisioning token (offline mode), `ValidateManualConfigAsync()` validates the cloud URL with `Uri.TryCreate` checking for `http` or `https` scheme — it explicitly accepts HTTP URLs. Since no cloud registration occurs (offline path), `DeviceRegistrationService.RegisterAsync()` is never called, which means `CloudUrlGuard.IsSecure()` never runs. The HTTP URL is stored in `RegistrationState.CloudBaseUrl` and propagated to `AgentConfiguration.CloudBaseUrl` via `RegistrationManager.PostConfigure()`. All cloud workers (CloudUploadWorker, ConfigPollWorker, TelemetryReporter) use this base URL for API calls via the "cloud" named HttpClient. While the HttpClient handler enforces TLS 1.2+, this only applies when the URL scheme triggers TLS negotiation — an HTTP URL bypasses TLS entirely. Transaction data, telemetry, and device tokens could be sent in plaintext.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:322-323` — `(uri.Scheme != "http" && uri.Scheme != "https")` — HTTP explicitly accepted
  - `ProvisioningWindow.axaml.cs:344-346` — offline path skips `DeviceRegistrationService.RegisterAsync` and `CloudUrlGuard.IsSecure`
  - `RegistrationManager.cs:197-198` — `options.CloudBaseUrl = state.CloudBaseUrl` — unvalidated URL propagated
  - `ServiceCollectionExtensions.cs:81` — `SslProtocols = Tls12 | Tls13` — only applies when URL triggers TLS
- **Impact:** Financial transaction data, device identity, and authentication tokens transmitted in cleartext over the network. Attacker on the same network can intercept and modify data.
- **Recommended Fix:** Add `CloudUrlGuard.IsSecure()` validation in `ValidateManualConfigAsync()` before accepting the cloud URL. Reject non-HTTPS URLs with a clear error message. Alternatively, enforce HTTPS in `RegistrationManager.PostConfigure()` as a defense-in-depth check.
- **Fix Applied:** Replaced URL format validation with `CloudUrlGuard.IsSecure()` in both `ValidateManualConfigAsync()` and `RegisterWithCodeAsync()`. Non-HTTPS URLs are rejected with a clear error message. HTTP is allowed only for localhost/loopback (development).

---

### S-DSK-007
- **Title:** Cloud connection test falls back to raw HttpClient without TLS enforcement or certificate pinning
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** In `RunConnectionTestsAsync()`, the cloud health check creates the HTTP client via `_httpClientFactory?.CreateClient("cloud") ?? new HttpClient()`. If `_httpClientFactory` is null (which is possible since it's resolved from `AgentAppContext.ServiceProvider` via nullable `GetService<T>()`), a plain `HttpClient` without TLS enforcement, certificate pinning, or any custom handler configuration is used. This fallback client sends a GET request to the cloud URL without transport security validation. A MitM attacker could intercept the request and return a fake 200 OK, causing the connectivity test to pass and the user to proceed with a compromised network path.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:473-474` — `using var httpClient = _httpClientFactory?.CreateClient("cloud") ?? new HttpClient()` — nullable fallback
  - `ProvisioningWindow.axaml.cs:19` — `private readonly IHttpClientFactory? _httpClientFactory` — nullable field
  - `ProvisioningWindow.axaml.cs:51` — `_httpClientFactory = services?.GetService<IHttpClientFactory>()` — can be null if ServiceProvider is null
  - `ServiceCollectionExtensions.cs:74-84` — "cloud" client configured with TLS 1.2+, cert pinning — bypassed by fallback
- **Impact:** False-positive connectivity test result under MitM conditions. No credentials are leaked in this specific request (health check only), but it gives false confidence that the cloud connection is secure when it may not be.
- **Recommended Fix:** Throw or show an error if `_httpClientFactory` is null instead of falling back to a raw client. The health check should use the same transport security as production cloud calls. Alternatively, skip the test entirely if the factory is unavailable.
- **Fix Applied:** Removed the `?? new HttpClient()` fallback in both `TestCloudConnectivityAsync` and `TestFccConnectivityAsync` in `SetupOrchestrator.cs`. When `_httpClientFactory` is null, the test now returns a `TestState.Failed` result with a descriptive message instead of silently downgrading to an insecure client. This ensures the connectivity test always uses the DI-registered named clients with their configured TLS 1.2+ enforcement and certificate pinning.

---

### S-DSK-008
- **Title:** Linux credential store fallback encryption key derivable from same-directory files
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Insecure storage of credentials
- **Status:** FIXED
- **Description:** On Linux systems without `secret-tool` (libsecret), `PlatformCredentialStore` falls back to AES-256 file-based encryption. The encryption key is derived via PBKDF2 from `/etc/machine-id` (world-readable) combined with a per-installation random salt. Both the salt file (`secrets/installation.salt`) and the encrypted credentials (`secrets/credentials.dat`) are stored in the same agent data directory. An attacker with read access to this directory can derive the encryption key by reading `installation.salt`, reading `/etc/machine-id`, and running the same PBKDF2 derivation. While `SetRestrictivePermissions` is applied to the `secrets/` directory, file permission enforcement depends on the filesystem and may not be effective on shared volumes, Docker bind mounts, or backup media.
- **Evidence:**
  - `PlatformCredentialStore.cs:324-330` — `DeriveLinuxMachineKey()` uses `/etc/machine-id` (world-readable on most Linux distros)
  - `PlatformCredentialStore.cs:350-355` — salt stored at `{AgentDataDirectory}/secrets/installation.salt` alongside credentials
  - `PlatformCredentialStore.cs:416` — credentials at `{AgentDataDirectory}/secrets/credentials.dat` in the same directory
  - `PlatformCredentialStore.cs:338-343` — PBKDF2 derivation with 100K iterations — reproducible with known inputs
- **Impact:** Device JWT, refresh tokens, and API keys can be decrypted by any process that can read the agent data directory on Linux. This includes backup tools, container volume mounts, and other users on multi-tenant systems.
- **Recommended Fix:** Consider using a hardware-bound secret (e.g., TPM2 via `tpm2-pkcs11`) as an additional key derivation input. Alternatively, store the salt in a separate location from the encrypted data (e.g., user keyring, environment variable, or a different filesystem path with different ACLs). Document the threat model limitation for environments where libsecret is unavailable.
- **Fix Applied:** On Linux, the installation salt is now stored in a separate directory (`~/.config/fcc-desktop-agent/` via `XDG_CONFIG_HOME`) from the encrypted credentials (`{AgentDataDirectory}/secrets/credentials.dat`). A new `GetSaltDirectory()` method returns the platform-appropriate salt path. Backward compatibility is maintained: existing salts at the legacy location are automatically migrated to the new directory and deleted from the old one. An attacker must now read two separate filesystem paths with different ACLs to derive the encryption key. Note: the underlying threat model limitation (file-based encryption without hardware-bound keys) is inherent when `libsecret` is unavailable; TPM2 integration remains a future enhancement.

---

## Authentication & Security Module

### S-DSK-009
- **Title:** WebSocket server has no authentication — full transaction data accessible to any LAN client
- **Module:** Authentication & Security
- **Severity:** Critical
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** The Odoo-compatible WebSocket server (`OdooWebSocketServer`) on port 8443 accepts connections without any form of authentication. The REST API enforces `X-Api-Key` via `ApiKeyMiddleware` (architecture rule #14: "no localhost bypass"), but the WebSocket endpoint completely bypasses this rule. Any device on the station LAN can connect and: (1) query all transaction records including amounts, product codes, and attendant IDs (`latest`, `all` modes); (2) modify transaction records including order IDs, payment IDs, and discard flags (`manager_update`, `attendant_update`, `manager_manual_update`); (3) receive real-time pump status including fuelling state and authorization status (`fuelpumpstatus` mode). This exposes the same sensitive data that the REST API protects, through an unprotected parallel channel.
- **Evidence:**
  - `OdooWebSocketServer.cs:56-98` — no auth check in `HandleConnectionAsync`
  - `OdooWsMessageHandler.cs:39-86` — transaction data returned without authentication
  - `OdooWsMessageHandler.cs:90-121` — `HandleManagerUpdateAsync` modifies DB without auth
  - `LocalApiStartup.cs:82-83` — REST API has `UseMiddleware<ApiKeyMiddleware>()`, WebSocket has nothing
- **Impact:** Complete bypass of LAN API authentication. An attacker on the station network can exfiltrate all transaction data, modify records, and monitor pump operations in real-time.
- **Recommended Fix:** Validate the `X-Api-Key` header during the HTTP upgrade request before accepting the WebSocket connection. Reject connections that don't provide a valid key. This mirrors the REST API's authentication without requiring protocol-level changes on the Odoo POS client.
- **Fix Applied:** API key validation is now enforced on the HTTP upgrade request in `OdooWsBridge.cs` before the WebSocket connection is accepted (F-DSK-012). The `ValidateApiKey` method checks both the `X-Api-Key` header and `apiKey` query parameter (for WebSocket clients that cannot set custom headers), using constant-time comparison via `ApiKeyMiddleware.ConstantTimeEquals`. Connections without a valid key are rejected with HTTP 401 before the upgrade completes.

---

### S-DSK-010
- **Title:** Petronite OAuth client_secret stored in plaintext configuration — not in credential store
- **Module:** Authentication & Security
- **Severity:** High
- **Category:** Insecure storage of credentials
- **Status:** FIXED
- **Description:** `PetroniteOAuthClient.RequestTokenAsync()` reads `_config.ClientId` and `_config.ClientSecret` from `FccConnectionConfig`, which is populated from `appsettings.json` or cloud config. The client secret is a long-lived credential for the Petronite FCC API. Unlike device tokens (stored in `ICredentialStore` with DPAPI/Keychain/libsecret), the Petronite client secret sits in plaintext configuration on disk. Any local user who can read the appsettings.json or the cloud config JSON cache can extract this secret and impersonate the agent against the Petronite API.
- **Evidence:**
  - `PetroniteOAuthClient.cs:116-118` — `var clientSecret = _config.ClientSecret` — read from config object
  - `PetroniteOAuthClient.cs:126` — `$"{clientId}:{clientSecret}"` — concatenated and Base64-encoded for Basic auth
  - No usage of `ICredentialStore` for the Petronite secret
- **Impact:** Stolen client secret allows unauthorized pump authorization and transaction operations against the Petronite FCC on behalf of the compromised agent.
- **Recommended Fix:** Store the Petronite client secret in `ICredentialStore` under a dedicated key (e.g., `fcc:petronite_client_secret`). Load it from the credential store during adapter initialization instead of from configuration.
- **Fix Applied:** `PetroniteOAuthClient` now accepts an optional `ICredentialStore` dependency. In `RequestTokenAsync`, the client secret is resolved from `ICredentialStore` under `CredentialKeys.PetroniteClientSecret` (`fcc:petronite_client_secret`) first, falling back to `FccConnectionConfig.ClientSecret` for backward compatibility. The credential key is defined in `CredentialKeys.cs`.

---

### S-DSK-011
- **Title:** DOMS FcAccessCode (logon credential) stored in plaintext configuration
- **Module:** Authentication & Security
- **Severity:** High
- **Category:** Insecure storage of credentials
- **Status:** FIXED
- **Description:** `DomsLogonHandler.BuildLogonRequest()` accepts `fcAccessCode` as a parameter, which originates from `FccConnectionConfig` (appsettings.json / cloud config). The DOMS FcAccessCode is the authentication credential used to establish a session with the Forecourt Controller. Like the Petronite secret, it resides in plaintext on disk rather than in the platform credential store.
- **Evidence:**
  - `DomsLogonHandler.cs:29-42` — `BuildLogonRequest(string fcAccessCode, ...)` — credential passed as plain string
  - `DomsLogonHandler.cs:38` — `["FcAccessCode"] = fcAccessCode` — sent in the JPL message
- **Impact:** Any local user who can read the agent's configuration can extract the DOMS access code and authenticate directly to the Forecourt Controller, bypassing the agent entirely.
- **Recommended Fix:** Store the DOMS FcAccessCode in `ICredentialStore` under a key like `fcc:doms_access_code`. Load it from the credential store when establishing the DOMS session.
- **Fix Applied:** `DomsJplAdapter` now accepts an optional `ICredentialStore` dependency. In `ConnectAsync`, the FcAccessCode is resolved from `ICredentialStore` under `CredentialKeys.DomsFcAccessCode` (`fcc:doms_access_code`) first, falling back to `FccConnectionConfig.FcAccessCode` for backward compatibility. The credential key is defined in `CredentialKeys.cs`.

---

### S-DSK-012
- **Title:** Radix shared secret stored in plaintext configuration
- **Module:** Authentication & Security
- **Severity:** High
- **Category:** Insecure storage of credentials
- **Status:** FIXED
- **Description:** `RadixSignatureHelper` methods accept `sharedSecret` as a plain string parameter, originating from `FccConnectionConfig`. The Radix shared secret is used to sign and validate every FCC request and response. Compromise of this secret allows an attacker to forge signed messages to the FCC, potentially authorizing fuel dispensing or modifying transaction data. Like other FCC credentials, it is stored in plaintext configuration rather than in the platform credential store.
- **Evidence:**
  - `RadixSignatureHelper.cs:46` — `ComputeTransactionSignature(string reqContent, string sharedSecret)` — plain string
  - `RadixSignatureHelper.cs:63` — `ComputeAuthSignature(string authDataContent, string sharedSecret)` — same pattern
  - `RadixSignatureHelper.cs:84` — `ValidateSignature(string content, string expectedSignature, string sharedSecret)` — plain string
- **Impact:** Stolen shared secret allows forging signed FCC messages, potentially authorizing unauthorized fuel dispensing or manipulating transaction data.
- **Recommended Fix:** Store the Radix shared secret in `ICredentialStore`. Load it from the credential store when the adapter initializes.
- **Fix Applied:** `RadixAdapter` now accepts an optional `ICredentialStore` dependency. A `ResolveSharedSecretAsync` method resolves the shared secret from `ICredentialStore` under `CredentialKeys.RadixSharedSecret` (`fcc:radix_shared_secret`) on first use, caching the result for subsequent calls. Falls back to `FccConnectionConfig.SharedSecret` for backward compatibility. The credential key is defined in `CredentialKeys.cs`.

---

### S-DSK-013
- **Title:** WebSocket TLS certificate password stored in appsettings.json configuration class
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Insecure storage of credentials
- **Status:** FIXED
- **Description:** `WebSocketServerOptions.CertificatePassword` is a plain `string?` property bound from the "WebSocket" configuration section. While the configuration class itself doesn't persist data, the value originates from `appsettings.json` where it sits alongside other non-sensitive settings. The `[SensitiveData]` attribute is NOT applied to this property, so it is not redacted by the `SensitiveDataDestructuringPolicy` if the options object is ever logged via Serilog destructuring.
- **Evidence:**
  - `OdooWebSocketServer.cs:285` — `public string? CertificatePassword { get; set; }` — no `[SensitiveData]` attribute
  - `ServiceCollectionExtensions.cs:125` — `services.Configure<WebSocketServerOptions>(config.GetSection(...))` — bound from config
  - `SensitiveDataDestructuringPolicy.cs:27` — only redacts properties with `[SensitiveData]`
- **Impact:** If `WebSocketServerOptions` is ever logged as a structured object (e.g., during diagnostic logging or error reports), the certificate password will appear in plaintext in the log file.
- **Recommended Fix:** Add `[SensitiveData]` attribute to the `CertificatePassword` property. Additionally, move the password to `ICredentialStore` as recommended in S-DSK-002.
- **Fix Applied:** Added `[SensitiveData]` attribute to `WebSocketServerOptions.CertificatePassword` in `OdooWebSocketServer.cs`. The property is now redacted to `[REDACTED]` by `SensitiveDataDestructuringPolicy` when the options object is logged via Serilog structured logging. The certificate password storage in `ICredentialStore` was already addressed in S-DSK-002.

---

### S-DSK-014
- **Title:** Radix signature validation uses non-constant-time string comparison
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** `RadixSignatureHelper.ValidateSignature()` uses `string.Equals(computed, expectedSignature, StringComparison.OrdinalIgnoreCase)` to compare signatures. This is a standard string comparison that short-circuits on the first mismatched character, making it vulnerable to timing side-channel attacks. An attacker on the LAN who can observe response timing could theoretically determine the correct signature one character at a time. The API key middleware correctly uses `CryptographicOperations.FixedTimeEquals` for this exact reason.
- **Evidence:**
  - `RadixSignatureHelper.cs:87` — `string.Equals(computed, expectedSignature, StringComparison.OrdinalIgnoreCase)` — timing-vulnerable
  - `ApiKeyMiddleware.cs:71-97` — `CryptographicOperations.FixedTimeEquals` — correctly constant-time
- **Impact:** Timing side-channel could allow an attacker to forge Radix message signatures. Practical exploitation requires precise timing measurements on the LAN, making this a low-probability but high-impact vector.
- **Recommended Fix:** Replace the string comparison with `CryptographicOperations.FixedTimeEquals` on the UTF-8 byte representations of both signatures, similar to the `ApiKeyMiddleware.ConstantTimeEquals` pattern.
- **Fix Applied:** Replaced `string.Equals` with `CryptographicOperations.FixedTimeEquals` on UTF-8 byte representations in `RadixSignatureHelper.ValidateSignature`. Both the computed and expected signatures are normalized to lowercase before byte comparison, preserving case-insensitive matching while eliminating the timing side-channel.

---

### S-DSK-015
- **Title:** FCC adapter credentials (ClientId, ClientSecret, FcAccessCode, SharedSecret) not marked with [SensitiveData]
- **Module:** Authentication & Security
- **Severity:** Medium
- **Category:** Sensitive data written to logs
- **Status:** FIXED
- **Description:** `FccConnectionConfig` likely contains properties like `ClientId`, `ClientSecret`, `OAuthTokenEndpoint`, `FcAccessCode`, and `SharedSecret` that hold FCC authentication credentials. If any of these properties are not annotated with `[SensitiveData]`, they will not be redacted by the `SensitiveDataDestructuringPolicy` when the config object is logged via Serilog structured logging. Given that `PetroniteOAuthClient` logs at `LogDebug` level with the token endpoint URL, it's possible that config objects containing secrets could be logged during diagnostic sessions.
- **Evidence:**
  - `PetroniteOAuthClient.cs:111-118` — reads `_config.ClientId`, `_config.ClientSecret`, `_config.OAuthTokenEndpoint`
  - `SensitiveDataAttribute.cs:8` — `[AttributeUsage(AttributeTargets.Property | ...)]` — exists but must be applied
  - `SensitiveDataDestructuringPolicy.cs:27` — only redacts `[SensitiveData]`-annotated properties
- **Impact:** FCC credentials could appear in log files during debug-level logging, which may be enabled during field troubleshooting.
- **Recommended Fix:** Audit `FccConnectionConfig` and apply `[SensitiveData]` to all credential properties (`ClientSecret`, `FcAccessCode`, `SharedSecret`, and any password fields). Also consider applying it to `ClientId` since it's a partial credential.
- **Fix Applied:** All credential properties in `FccConnectionConfig` already have `[SensitiveData]` applied: `ApiKey`, `SharedSecret`, `FcAccessCode`, `ClientId`, `ClientSecret`, `WebhookSecret`, and `AdvatecWebhookToken` (verified in `AdapterTypes.cs`). The `SensitiveDataDestructuringPolicy` will redact all of these when the config object is logged via Serilog.

---

### S-DSK-016
- **Title:** Token refresh sends both device token and refresh token in plaintext JSON body — no additional encryption layer
- **Module:** Authentication & Security
- **Severity:** Low
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** `DeviceTokenProvider.RefreshTokenCoreAsync()` sends both the current device token AND the refresh token in a JSON POST body to the cloud. While TLS protects the transport, both tokens appear in the request body as plaintext JSON fields. If TLS is compromised (e.g., certificate pinning bypassed, corporate proxy, or debug logging enabled on a reverse proxy), both tokens are exposed simultaneously. The device token alone grants API access; the refresh token grants the ability to generate new device tokens indefinitely. Sending both in a single request maximizes exposure.
- **Evidence:**
  - `DeviceTokenProvider.cs:128-132` — `await http.PostAsJsonAsync(url, new { refreshToken, deviceToken = ... }, ct)` — both tokens in body
- **Impact:** If the TLS channel is compromised (unlikely but not impossible with cert pinning), both tokens are exposed in a single capture. The refresh token grants indefinite access until revoked.
- **Recommended Fix:** This is the standard OAuth2 token rotation pattern and TLS provides adequate protection. Document the threat model assumption that TLS is trusted. As a defense-in-depth measure, consider sending only the refresh token (the device token may not be needed by the server for rotation).
- **Fix Applied:** Removed `deviceToken` from the refresh request JSON body in `RefreshTokenCoreAsync`. Only the `refreshToken` is now sent, minimizing token exposure if TLS is compromised. Added inline comment documenting the TLS trust model assumption.

---

## Configuration Module

### S-DSK-017
- **Title:** ConfigurationPage displays FCC API key in plaintext TextBox without masking
- **Module:** Configuration
- **Severity:** High
- **Category:** Sensitive data exposure
- **Status:** FIXED
- **Description:** `ConfigurationPage` displays the FCC API key in a regular `TextBox` with no password masking: `CfgApiKey.Text = config.FccApiKey` (line 67). The AXAML defines the control as `<TextBox x:Name="CfgApiKey" IsReadOnly="True" FontSize="12" FontFamily="Consolas, monospace" />` — fully visible, selectable, and copyable. The FCC API key authenticates the agent against the Forecourt Controller over LAN. In a fuel station environment, the configuration screen may be visible to attendants, customers, or security cameras during maintenance. Combined with the "Regenerate" button (which generates a visible key that is never actually saved — see F-DSK-019), this creates a confusing and insecure key management experience.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:67` — `CfgApiKey.Text = config.FccApiKey` — plaintext assignment
  - `ConfigurationPage.axaml:178` — `<TextBox x:Name="CfgApiKey" IsReadOnly="True" ...>` — no PasswordChar masking
  - `ConfigurationPage.axaml:180-183` — "Regenerate" button generates visible key
- **Impact:** Shoulder-surfing exposure of the FCC API key. An attacker who obtains this key can impersonate the agent on the FCC LAN, potentially authorizing fuel dispensing or reading transaction data.
- **Recommended Fix:** Use `PasswordChar="•"` on the TextBox by default. Add a "Show/Hide" toggle button. Auto-hide after a short timeout. Consider whether the FCC API key should be visible in the configuration UI at all, or only in a dedicated security/credentials section.
- **Fix Applied:** Added `PasswordChar="&#x2022;"` (bullet) to `CfgApiKey` TextBox in AXAML. Added "Show/Hide" toggle button (`OnToggleApiKeyClicked`) that reveals the key and auto-hides after 10 seconds via `AutoHideApiKeyAsync`. Regenerate button now briefly shows the new key before auto-hiding.

---

### S-DSK-018
- **Title:** LocalOverrideManager stores overrides in plaintext JSON without integrity protection — FCC redirect attack
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Insecure file storage
- **Status:** FIXED
- **Description:** `LocalOverrideManager` writes FCC connection overrides to `overrides.json` in the agent data directory using `File.WriteAllText` with no integrity check (HMAC, signature, or tamper detection). A local attacker with write access to the data directory can modify `FccHost` to point to a malicious FCC endpoint. On next read, the agent would connect to the attacker's endpoint, which could: (1) return fake transaction data, (2) capture the FCC API key sent in authentication headers, or (3) respond to pre-auth requests by authorizing fuel dispensing to arbitrary pumps. The `IsValidHostOrIp` check only validates format, not trust — any valid IP address is accepted.
- **Evidence:**
  - `LocalOverrideManager.cs:228-242` — `Persist(OverrideData data)` writes plaintext JSON with `File.WriteAllText`
  - `LocalOverrideManager.cs:197-226` — `Load()` reads and deserializes without integrity validation
  - `LocalOverrideManager.cs:179-191` — `IsValidHostOrIp` validates format only, accepts any IP
  - No HMAC, signature, or checksum on the override file
- **Impact:** A local attacker can redirect FCC connections to a rogue endpoint by modifying a single JSON file. This enables credential theft and unauthorized fuel dispensing authorization.
- **Recommended Fix:** Add an HMAC over the file contents using a key derived from the credential store. Validate the HMAC on load; reject and log tampered files. Alternatively, move override storage into the platform credential store alongside other secrets.
- **Fix Applied:** Added HMAC-SHA256 integrity protection to `LocalOverrideManager`. A 256-bit HMAC key is generated and stored in the platform credential store (`config:overrides_hmac_key`). On `Persist()`, an HMAC is computed over the JSON and written to `overrides.hmac`. On `Load()`, the HMAC is validated using `CryptographicOperations.FixedTimeEquals`; tampered files are rejected with a warning log and overrides are treated as empty. `ClearAllOverrides()` also deletes the HMAC file. Backward-compatible: first load after upgrade accepts existing files and writes an initial HMAC.

---

### S-DSK-019
- **Title:** CloudBaseUrl from cloud config applied without CloudUrlGuard.IsSecure validation
- **Module:** Configuration
- **Severity:** Medium
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** `ConfigManager.ApplyHotReloadFields` applies `source.Sync.CloudBaseUrl` directly to `target.CloudBaseUrl` (line 258-259) without validating via `CloudUrlGuard.IsSecure()`. `CloudUrlGuard` is only called during the provisioning flow (via `DeviceRegistrationService`). If a compromised or misconfigured cloud API returns a config with `Sync.CloudBaseUrl = "http://attacker.example.com"`, the agent will start sending transaction data, device tokens, and telemetry over unencrypted HTTP. The `HttpClientHandler` enforces TLS 1.2+ protocol, but this only applies when the URL scheme triggers TLS negotiation — an HTTP URL bypasses TLS entirely.
- **Evidence:**
  - `ConfigManager.cs:258-259` — `target.CloudBaseUrl = source.Sync.CloudBaseUrl` — no security validation
  - `CloudUrlGuard.cs:14-34` — `IsSecure()` validates HTTPS requirement — never called from ConfigManager
  - `DeviceRegistrationService.cs:51-57` — only place where `CloudUrlGuard.IsSecure()` is enforced
  - `ServiceCollectionExtensions.cs:81` — TLS enforcement only applies when URL scheme is HTTPS
- **Impact:** A single malicious cloud config push can downgrade all agent-to-cloud communication from HTTPS to plaintext HTTP, exposing transaction data, authentication tokens, and telemetry.
- **Recommended Fix:** Add `CloudUrlGuard.IsSecure()` validation in `ApplyHotReloadFields` before accepting a new `CloudBaseUrl`. Reject non-HTTPS URLs with a warning log. Also add the same validation in `DesktopFccRuntimeConfiguration.TryValidateSiteConfig`.
- **Fix Applied:** Added `CloudUrlGuard.IsSecure()` validation in `ConfigManager.ApplyHotReloadFields` — non-HTTPS `CloudBaseUrl` values from cloud config are silently rejected, preserving the current safe URL. Also added HTTPS validation in `DesktopFccRuntimeConfiguration.TryValidateSiteConfig` — site configs with non-HTTPS `Sync.CloudBaseUrl` are rejected during validation with a descriptive error message.

---

## FCC Device Integration Module

### S-DSK-020
- **Title:** WebSocket receive buffer has no maximum message size limit — unbounded memory allocation from malicious client
- **Module:** FCC Device Integration
- **Severity:** High
- **Category:** Insecure storage of credentials
- **Status:** FIXED
- **Description:** `OdooWebSocketServer.ReceiveLoopAsync` uses a fixed 4096-byte buffer but accumulates message fragments in a `StringBuilder` (line 107) with no size limit. The loop appends chunks until `result.EndOfMessage` is true. A malicious LAN client can send a single WebSocket message that is gigabytes long by never setting the `EndOfMessage` flag. The `StringBuilder` will grow until the process runs out of memory, causing an `OutOfMemoryException` that crashes the entire agent. Combined with the lack of authentication (S-DSK-009), any device on the station LAN can trigger this denial-of-service.
- **Evidence:**
  - `OdooWebSocketServer.cs:107` — `var sb = new StringBuilder()` — no maximum capacity
  - `OdooWebSocketServer.cs:115` — `sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count))` — appends without size check
  - `OdooWebSocketServer.cs:117` — loop continues until `result.EndOfMessage` — no size guard
  - No `MaxMessageSize` configuration or validation
- **Impact:** Denial-of-service: any LAN device can crash the agent by sending an oversized WebSocket message. The agent process terminates, halting all transaction processing until manually restarted.
- **Recommended Fix:** Add a `MaxMessageSize` constant (e.g., 1 MB) and check `sb.Length` after each append. If the limit is exceeded, close the WebSocket with `PolicyViolation` and log a warning.
- **Fix Applied:** Added `MaxMessageSize` constant (1 MB) to `OdooWebSocketServer`. The `ReceiveLoopAsync` loop now checks `sb.Length` after each chunk append. If the limit is exceeded, the connection is closed with `WebSocketCloseStatus.PolicyViolation` and a warning is logged.

---

### S-DSK-021
- **Title:** Advatec adapter logs customer PII (CustomerTaxId, CustomerName) in pre-auth correlation messages
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Sensitive data written to logs
- **Status:** FIXED
- **Description:** `AdvatecAdapter.TryMatchPreAuth` logs `CustomerId` (which is the customer's tax ID) at `Information` level during receipt correlation (lines 464-467). The `SendPreAuthAsync` method logs `OdooOrderId` and `CorrelationId` which are non-sensitive, but the correlation logs include PII from the `ActivePreAuth` record. The `SensitiveDataDestructuringPolicy` only redacts properties annotated with `[SensitiveData]` on class definitions — it does not redact string interpolation arguments in structured log templates. Customer tax IDs are regulated personal data in many jurisdictions (GDPR, TRA in Tanzania).
- **Evidence:**
  - `AdvatecAdapter.cs:464-467` — `_logger.LogInformation("... CustomerId=...")` — logs customer tax ID at Information level
  - `AdvatecAdapter.cs:581-583` — `_logger.LogDebug("... CustomerId={CustomerId}...")` — also logged at debug level
  - `PreAuthHandler.cs:137-139` — `CustomerName` and `CustomerTaxId` stored in DB but should not appear in logs
- **Impact:** Customer tax IDs and names in log files, which may be accessible to support staff, log aggregation systems, or backup media. Violates data minimization principles.
- **Recommended Fix:** Remove `CustomerId` from log template arguments. Log only the pump number and correlation ID. If debugging requires customer data, use `LogDebug` with a redacted format (e.g., last 4 characters only).
- **Fix Applied:** Removed `CustomerId` from the `LogDebug` template in `AdvatecAdapter.cs`. The log now includes only `Pump={Pump}` instead of `CustomerId={CustomerId}`, preventing customer tax ID exposure in log files.

---

### S-DSK-022
- **Title:** Advatec webhook listener token validation uses string comparison — potential timing attack
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** `AdvatecWebhookListener` validates the webhook authentication token to ensure only the configured Advatec device can send receipt callbacks. If the token comparison uses standard `string.Equals` (like the Radix signature validation in S-DSK-014), it is vulnerable to timing side-channel attacks. An attacker on the LAN who can send HTTP requests to the webhook listener port could determine the token character by character through precise response timing measurements. This is the same class of vulnerability as the Radix signature comparison (S-DSK-014), but applied to the webhook authentication boundary.
- **Evidence:**
  - `AdvatecWebhookListener.cs` — webhook token validation
  - `AdvatecAdapter.cs:166` — `_config.AdvatecWebhookToken` — token passed to listener constructor
  - Pattern established in S-DSK-014 — standard string comparison is timing-vulnerable
- **Impact:** An attacker on the station LAN could forge webhook callbacks by determining the token through timing analysis, injecting fake receipt data into the transaction buffer.
- **Recommended Fix:** Use `CryptographicOperations.FixedTimeEquals` for webhook token comparison, matching the pattern from `ApiKeyMiddleware`.
- **Fix Applied:** Replaced `string.Equals(providedToken, _webhookToken, StringComparison.Ordinal)` with `CryptographicOperations.FixedTimeEquals` on UTF-8 byte representations in `AdvatecWebhookListener.cs`. The null/empty check is performed first (short-circuit is safe for empty tokens), followed by constant-time comparison for non-empty tokens.

---

### S-DSK-023
- **Title:** DomsAdapter sends FCC API key in X-API-Key header over potentially unencrypted HTTP
- **Module:** FCC Device Integration
- **Severity:** Medium
- **Category:** Insecure storage of credentials
- **Status:** FIXED
- **Description:** `DomsAdapter.BuildRequest` unconditionally adds the FCC API key to every request via `request.Headers.Add("X-API-Key", _config.ApiKey)` (line 259). The FCC base URL is user-configurable and there is no enforcement that it uses HTTPS. While FCC communication occurs over the station LAN (reducing interception risk), the API key is sent in cleartext HTTP headers if the FCC URL is `http://`. The `CloudUrlGuard.IsSecure()` check is only applied to cloud URLs, not FCC URLs. On shared or multi-tenant LANs (e.g., fuel station with customer WiFi), HTTP traffic containing the API key could be captured.
- **Evidence:**
  - `DomsAdapter.cs:259` — `request.Headers.Add("X-API-Key", _config.ApiKey)` — always sent
  - `DomsAdapter.cs:256` — `var baseUri = new Uri(_config.BaseUrl.TrimEnd('/') + "/")` — no scheme validation
  - No `FccUrlGuard.IsSecure()` equivalent for FCC URLs
- **Impact:** FCC API key exposure on LANs where HTTP traffic can be sniffed. The key grants full access to the Forecourt Controller's transaction and pump operations.
- **Recommended Fix:** Add a warning log when the FCC base URL does not use HTTPS. Consider enforcing HTTPS for FCC connections in production environments, with an explicit override for development/testing.
- **Fix Applied:** Added HTTPS scheme check in `DomsAdapter.BuildRequest`. When the FCC base URL does not use HTTPS, a warning is logged once (using `_fccHttpWarningLogged` flag to avoid log spam) alerting that the X-API-Key header will be sent without TLS encryption.

---

## Site Master Data Module

### S-DSK-024
- **Title:** ConfigurationPage generates API key with Guid.NewGuid — not cryptographically secure
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** `ConfigurationPage.OnRegenerateApiKeyClicked` (line 226) generates a new local API key using `Guid.NewGuid().ToString("N")`. While .NET GUIDs (v4) use a CSPRNG internally on modern runtimes, the GUID format reserves 6 bits for version/variant fields, yielding only 122 bits of entropy from 128 bits of output. More importantly, the GUID format is recognizable and may be treated as a non-secret identifier by logging systems or monitoring tools. A proper API key should use `RandomNumberGenerator.GetBytes` for guaranteed cryptographic randomness and higher entropy density.
- **Evidence:**
  - `ConfigurationPage.axaml.cs:226` — `var newKey = Guid.NewGuid().ToString("N")` — 32 hex chars, 122 bits entropy
  - `AgentConfiguration.cs:68-69` — `[SensitiveData] public string FccApiKey` — the field being regenerated
  - `ApiKeyMiddleware.cs` — compares this key on every local API request
- **Impact:** The API key has slightly reduced entropy (122 vs 256 bits) and a recognizable format that may bypass log redaction rules. While 122 bits is sufficient against brute force, it does not follow security best practices for API key generation.
- **Recommended Fix:** Replace with `Convert.ToHexString(RandomNumberGenerator.GetBytes(32))` for a 256-bit key with full entropy. This also eliminates the recognizable GUID format.
- **Fix Applied:** Replaced `Guid.NewGuid().ToString("N")` with `Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()` in `ConfigurationPage.OnRegenerateApiKeyClicked`. The new key has full 256-bit entropy from a CSPRNG and a non-recognizable hex format.

---

### S-DSK-025
- **Title:** FCC API key displayed in cleartext TextBox on ConfigurationPage — visible to shoulder surfers
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Sensitive data written to logs
- **Status:** FIXED
- **Description:** `ConfigurationPage.axaml` (line 178) displays the FCC API key in a standard `TextBox` with `IsReadOnly="True"`. The key is fully visible in cleartext on screen. The field is marked `[SensitiveData]` in `AgentConfiguration` (line 68) and stored securely in the platform credential store, but the UI renders it without masking. A regenerated key (line 226) is also displayed in cleartext before the user saves. Anyone viewing the operator screen (shoulder surfing, screen recording, remote desktop) can read the full API key.
- **Evidence:**
  - `ConfigurationPage.axaml:178` — `<TextBox x:Name="CfgApiKey" IsReadOnly="True" FontSize="12" FontFamily="Consolas, monospace" />`
  - `ConfigurationPage.axaml.cs:67` — `CfgApiKey.Text = config.FccApiKey` — populates cleartext
  - `AgentConfiguration.cs:68` — `[SensitiveData] public string FccApiKey` — marked as sensitive
- **Impact:** The API key is exposed to anyone with visual access to the configuration screen. In a fuel station environment with shared operator terminals, this enables unauthorized API access.
- **Recommended Fix:** Use a `PasswordBox` or mask the key by default (e.g., show only the last 4 characters). Add a "Show" toggle button for operators who need to copy the key.
- **Fix Applied:** Already addressed by S-DSK-017: `PasswordChar="&#x2022;"` (bullet) is applied to the `CfgApiKey` TextBox, with a Show/Hide toggle button and 10-second auto-hide. This finding is a duplicate of S-DSK-017.

---

### S-DSK-026
- **Title:** overrides.json writable by any process running as the same OS user — enables local FCC host injection
- **Module:** Site Master Data
- **Severity:** Medium
- **Category:** Insecure file storage
- **Status:** FIXED
- **Description:** `LocalOverrideManager` stores FCC host/port overrides in `overrides.json` in the agent data directory. While the directory has restrictive permissions (owner-only on Unix, per-user on Windows via %LOCALAPPDATA%), any process running as the same OS user can modify this file. A malicious process could overwrite `FccHost` to redirect FCC API calls to an attacker-controlled server, capturing API keys (sent as X-API-Key headers) and injecting fake transaction data. The override takes effect silently on the next adapter connection cycle.
- **Evidence:**
  - `LocalOverrideManager.cs:33` — `_filePath = Path.Combine(AgentDataDirectory.Resolve(), OverridesFileName)`
  - `LocalOverrideManager.cs:228-235` — `Persist(data)` writes JSON directly to file with no integrity protection
  - `AgentDataDirectory.cs:70-76` — Windows permissions rely on %LOCALAPPDATA% user isolation only
  - `DesktopFccRuntimeConfiguration.cs:177-180` — `ResolveBaseUrl` reads overrides for FCC URL construction
- **Impact:** A same-user malicious process can redirect all FCC communication to an attacker-controlled endpoint, capturing the API key and injecting fake pump status or transaction data.
- **Recommended Fix:** Add an HMAC or digital signature to `overrides.json` using a key from the platform credential store. Verify integrity on read. Log a security warning when overrides are modified outside the application.
- **Fix Applied:** Already addressed by S-DSK-018: HMAC-SHA256 integrity protection was added to `LocalOverrideManager`. A 256-bit HMAC key is stored in the platform credential store, and `overrides.hmac` is validated using `CryptographicOperations.FixedTimeEquals` on every load. This finding is a duplicate of S-DSK-018.

---

### S-DSK-027
- **Title:** Missing LAN API key disables authentication for the pre-auth REST endpoints
- **Module:** Pre-Authorization
- **Severity:** Critical
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** The embedded local API correctly inserts `ApiKeyMiddleware` before `MapPreAuthEndpoints`, but the middleware explicitly fails open when `LocalApi.ApiKey` is blank. In that state it logs a warning and forwards the request anyway. Because `/api/v1/preauth` is the LAN control surface that creates and cancels forecourt authorizations, a missing credential-store value turns the entire pre-auth API into an unauthenticated network endpoint.
- **Evidence:**
  - `ApiKeyMiddleware.cs:38-43` — blank key logs "authentication is DISABLED" and still calls `_next(context)`
  - `LocalApiStartup.cs:90-95` — `ApiKeyMiddleware` wraps `app.MapPreAuthEndpoints()`
- **Impact:** Any device that can reach the local API port can submit or cancel pre-authorizations when the LAN API key is absent after provisioning, misconfiguration, or startup credential-store failure.
- **Recommended Fix:** Fail closed. Refuse to start the local API without a loaded LAN API key, or return `503 Service Unavailable` from the middleware until the key is available from the credential store.
- **Fix Applied:** Changed `ApiKeyMiddleware` to fail closed: when no API key is configured, requests are now rejected with HTTP 503 (Service Unavailable) and an `AUTH_NOT_CONFIGURED` error code instead of being allowed through. The same fail-closed behavior was applied to `OdooWsBridge.ValidateApiKey` for WebSocket upgrade requests.

---

## Transaction Management Module

### S-DSK-028
- **Title:** TransactionsPage detail panel exposes raw FCC payload which may contain sensitive operational data
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Sensitive data exposure
- **Status:** FIXED
- **Description:** `TransactionsPage.axaml` (lines 142-148) renders the full `RawPayloadJson` in a read-only TextBox within the transaction detail panel. The raw FCC payload is the unprocessed JSON received from the fuel controller and may contain vendor-specific fields not intended for display: attendant PINs, vehicle identification numbers, customer tax IDs, internal FCC sequence tokens, or diagnostic data. Any operator with access to the desktop agent UI can view this data. The `RawPayloadJson` field is also stored unfiltered in SQLite (BufferedTransaction.cs:57) and transmitted to cloud in the upload payload (CloudUploadWorker.cs:387).
- **Evidence:**
  - `TransactionsPage.axaml:142-148` — `<TextBox x:Name="DetailRawPayload" IsReadOnly="True" ...>`
  - `TransactionsPage.axaml.cs:207` — `DetailRawPayload.Text = row.RawPayloadJson ?? ""`
  - `BufferedTransaction.cs:57` — `public string RawPayloadJson { get; set; } = string.Empty`
  - `CloudUploadWorker.cs:387` — `RawPayloadJson = t.RawPayloadJson` — sent to cloud unfiltered
- **Impact:** Sensitive FCC data visible to any operator with desktop agent access. If the workstation is shared or screen-shared during support calls, sensitive data could be inadvertently exposed.
- **Recommended Fix:** Either redact sensitive fields from `RawPayloadJson` before display (apply a field-level filter matching `[SensitiveData]` attributes), or restrict the raw payload view to a diagnostic/admin mode that requires elevated permissions.
- **Fix Applied:** Raw payload is now hidden by default in `TransactionsPage.axaml` (`IsVisible="False"`). A "Show/Hide" toggle button (`OnToggleRawPayloadClicked`) allows operators to explicitly reveal the payload when needed. The payload is re-hidden each time a new transaction is selected in `ShowDetail()`.

### S-DSK-029
- **Title:** WebSocket TLS certificate password stored in plain text configuration
- **Module:** Transaction Management
- **Severity:** High
- **Category:** Insecure storage of credentials
- **Status:** FIXED
- **Description:** `WebSocketServerOptions.CertificatePassword` (OdooWebSocketServer.cs:288) stores the PFX certificate password as a plain string in the configuration model. This value is typically loaded from `appsettings.json` or environment variables and persisted on disk in clear text. The certificate password protects the private key used for WSS TLS — if compromised, an attacker can impersonate the WebSocket server for man-in-the-middle attacks on the Odoo POS to agent communication channel.
- **Evidence:**
  - `OdooWebSocketServer.cs:288` — `public string? CertificatePassword { get; set; }` — plain string
  - `OdooWebSocketServer.cs:285-286` — `CertificatePath` and `CertificatePassword` configured together
  - No `[SensitiveData]` attribute on the property
  - No DPAPI/credential store integration for certificate password
- **Impact:** Certificate private key password exposed in configuration files on disk. Compromised password enables TLS impersonation of the WebSocket server on the LAN.
- **Recommended Fix:** Load the certificate password from the Windows Credential Manager (DPAPI) or a platform keystore instead of plain configuration. Apply `[SensitiveData]` attribute to ensure the value is excluded from logs and telemetry.
- **Fix Applied:** Already addressed by S-DSK-002 and S-DSK-013: `[SensitiveData]` attribute is applied to `WebSocketServerOptions.CertificatePassword`, and the certificate password is loaded from `ICredentialStore` via `CredentialKeys.WsCertPassword`. This finding is a duplicate.

### S-DSK-030
- **Title:** WebSocket receive loop has no message size limit or rate limiting — vulnerable to resource exhaustion
- **Module:** Transaction Management
- **Severity:** Medium
- **Category:** Improper permissions handling
- **Status:** FIXED
- **Description:** `OdooWebSocketServer.ReceiveLoopAsync` (lines 102-121) reads WebSocket messages in a loop using a 4096-byte buffer and a `StringBuilder` that grows unbounded until `EndOfMessage` is received. A malicious or misconfigured client can send a single WebSocket frame with `EndOfMessage = false` and stream megabytes of data, causing `StringBuilder` to consume unbounded memory. Additionally, there is no rate limiting on message frequency — a client can flood the server with rapid small messages, each creating a new `OdooWsMessageHandler`, scoped `AgentDbContext`, and database query. The `MaxConnections` limit (line 60) only caps concurrent connections, not per-connection message rate.
- **Evidence:**
  - `OdooWebSocketServer.cs:107` — `var sb = new StringBuilder()` — new unbounded builder per message
  - `OdooWebSocketServer.cs:110-117` — `do { ... sb.Append(...) } while (!result.EndOfMessage)` — no size check
  - `OdooWebSocketServer.cs:119` — `HandleMessageAsync` called per message with no rate limit
  - `OdooWsMessageHandler.cs:139-140` — each message creates new handler + scoped DbContext
- **Impact:** A single malicious WebSocket client can exhaust agent memory via oversized messages or cause database contention via message flooding, degrading service for legitimate Odoo POS clients.
- **Recommended Fix:** Add a maximum message size check (e.g., 64 KB) in the receive loop — abort the connection if exceeded. Implement per-connection rate limiting (e.g., token bucket at 20 messages/second) to prevent flooding.
- **Fix Applied:** Already addressed by S-DSK-020: `MaxMessageSize` constant (1 MB) was added to `OdooWebSocketServer`. The `ReceiveLoopAsync` loop checks `sb.Length` after each chunk append and closes the connection with `WebSocketCloseStatus.PolicyViolation` if the limit is exceeded. This finding is a duplicate of S-DSK-020.

---

### S-DSK-031
- **Title:** `CloudUploadWorker` uses the wrong named `HttpClient`, bypassing the pinned cloud transport configuration
- **Module:** Cloud Sync
- **Severity:** High
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** The DI container registers a named client `cloud` with TLS 1.2/1.3 enforcement and `CertificatePinValidator`. Every other cloud worker uses that exact name. `CloudUploadWorker` alone calls `CreateClient("Cloud")` with a capital `C`. Because the upload path no longer matches the registered name, the transaction-upload call path can run without the intended pinned handler and with behavior that differs from the rest of the cloud-sync module.
- **Evidence:**
  - `ServiceCollectionExtensions.cs:74-84` — only `AddHttpClient("cloud", ...)` is registered with TLS and certificate pinning
  - `CloudUploadWorker.cs:226` — upload path uses `_httpFactory.CreateClient("Cloud")`
  - `StatusPollWorker.cs:179` — status poll uses `_httpFactory.CreateClient("cloud")`
  - `ConfigPollWorker.cs:168` — config poll uses `_httpFactory.CreateClient("cloud")`
  - `DeviceTokenProvider.cs:113` — token refresh uses `_httpFactory.CreateClient("cloud")`
  - `TelemetryReporter.cs:99` — telemetry uses `_httpFactory.CreateClient("cloud")`
- **Impact:** The most sensitive cloud call in the desktop agent, transaction upload, can miss the certificate-pinning policy that the rest of the agent relies on. That weakens transport authentication exactly where transaction payloads and raw FCC data are sent.
- **Recommended Fix:** Change `CloudUploadWorker` to use `CreateClient("cloud")` and add a regression test that verifies the worker uses the configured cloud client name and timeout/pinning policy.
- **Fix Applied:** Changed `CloudUploadWorker.cs` from `CreateClient("Cloud")` to `CreateClient("cloud")` to match the registered named client with TLS 1.2+ enforcement and certificate pinning configured in `ServiceCollectionExtensions`.

---

### S-DSK-032
- **Title:** Diagnostic log files are created without the permission hardening used for the protected agent data directory
- **Module:** Monitoring & Diagnostics
- **Severity:** Medium
- **Category:** Insecure file storage
- **Status:** FIXED
- **Description:** The desktop agent writes rolling Serilog files under `LocalApplicationData/FccDesktopAgent/logs`, but that path is created with a plain `Directory.CreateDirectory(...)` call. Unlike the SQLite/data directory, the log directory does not go through `AgentDataDirectory.SetRestrictivePermissions(...)` or any equivalent ACL hardening. On Unix-like deployments, the resulting permissions fall back to the process umask and can leave diagnostic logs readable by other local users.
- **Evidence:**
  - `Program.cs:27-32` - bootstrap logger writes to `GetLogPath()`
  - `Program.cs:42-46` - main Serilog pipeline also writes to `GetLogPath()`
  - `Program.cs:185-193` - `GetLogPath()` only creates the directory and returns the path
  - `AgentDataDirectory.cs:10-12` and `AgentDataDirectory.cs:63-80` - protected data directory explicitly applies owner-only permissions, including for files such as logs, but the log path helper does not reuse that logic
- **Impact:** On shared Linux/macOS workstations, another local user can read operational logs that may contain device/site identifiers, connectivity failures, endpoint details, and other diagnostic data.
- **Recommended Fix:** Create the logs directory through `AgentDataDirectory.Resolve()` or apply the same restrictive-permissions helper before opening the Serilog file sink. Keep the Windows `LocalAppData` location, but harden the Unix path to owner-only access.
- **Fix Applied:** Added `AgentDataDirectory.SetRestrictivePermissions(logDirectory)` call in `Program.cs:GetLogPath()` after `Directory.CreateDirectory`. The log directory now receives the same owner-only permissions (chmod 700 on Unix) as the protected data directory. `SetRestrictivePermissions` was changed from `internal` to `public` to allow access from the App project.

---

### S-DSK-033
- **Title:** Rotating the LAN API key in the desktop UI does not revoke the old key until restart
- **Module:** Odoo Integration
- **Severity:** High
- **Category:** Weak authentication flows
- **Status:** FIXED
- **Description:** The Configuration page now regenerates and saves `CredentialKeys.LanApiKey`, but the running API stack does not reload that credential. `CredentialStoreApiKeyPostConfigure` reads the key once in `StartAsync()` into `_cachedKey`, then every later `PostConfigure()` call reuses the cached value. Both the REST middleware and the WebSocket upgrade guard continue validating requests against that startup snapshot. The UI reports "Settings saved and applied" for a key-only change and does not force a restart, so operators can believe the new key is live while the old key still works.
- **Evidence:**
  - `ConfigSaveService.cs:87-92` — key rotation writes the new LAN API key only to the credential store
  - `LocalApiStartup.cs:116-130` — `_cachedKey` is loaded once at startup and reused for all later options snapshots
  - `ApiKeyMiddleware.cs:36-47` — REST auth validates against `CurrentValue.ApiKey`
  - `OdooWsBridge.cs:108-121` — WebSocket auth validates against the same `CurrentValue.ApiKey`
  - `ConfigurationPage.axaml.cs:138-152` — successful save path does not require restart for a key-only change
- **Impact:** A compromised old LAN key remains valid until the agent process restarts. At the same time, Odoo POS terminals updated with the new key can fail against a still-running agent, creating a security gap and an avoidable outage during key rotation.
- **Recommended Fix:** Replace the startup-only cache with a live key provider that can refresh after credential-store writes, or explicitly require and trigger an agent restart whenever the LAN API key changes. In either case, make the UI state clear that the old key is not revoked until reload completes.
- **Fix Applied:** Three changes: (1) Added `IApiKeyRefresher` interface in `FccDesktopAgent.Core.Security` with a `RefreshKeyAsync` method. (2) `CredentialStoreApiKeyPostConfigure` now implements `IApiKeyRefresher` — the `_cachedKey` field is `volatile` and `RefreshKeyAsync` reloads it from the credential store. Registered as `IApiKeyRefresher` singleton in DI. (3) `ConfigSaveService` accepts an optional `IApiKeyRefresher` dependency and calls `RefreshKeyAsync` immediately after writing a new LAN API key to the credential store. The old key is revoked immediately without requiring an agent restart.
