# Desktop Security Findings

> Security audit of the FCC Desktop Edge Agent.

---

## Application Shell Module

### S-DSK-001
- **Title:** API key displayed in plaintext on provisioning summary screen
- **Module:** Application Shell
- **Severity:** High
- **Category:** Sensitive data exposure
- **Description:** The provisioning wizard Step 4 displays the generated LAN API key in a readonly `TextBox` with no masking. The key is fully visible to anyone who can see the screen. In a fuel station environment, the setup screen may be visible to attendants or passers-by during installation. The key provides full access to the agent's local REST API, which controls transaction queries and pump operations.
- **Evidence:**
  - `ProvisioningWindow.axaml:284-288` — `<TextBox x:Name="ApiKeyDisplay" IsReadOnly="True" FontFamily="Consolas, monospace">` — no PasswordChar masking
  - `ProvisioningWindow.axaml.cs:611` — `ApiKeyDisplay.Text = _apiKey` — raw key set as visible text
- **Impact:** Shoulder-surfing exposure of the LAN API key, which can be used to query transactions and control pump operations from any device on the local network.
- **Recommended Fix:** Mask the key by default (`PasswordChar="*"`), add a "Show/Hide" toggle button, and auto-hide after a timeout.

---

### S-DSK-002
- **Title:** TLS certificate password stored in plaintext configuration
- **Module:** Application Shell
- **Severity:** High
- **Category:** Insecure storage of credentials
- **Description:** The WebSocket TLS certificate password is read from `appsettings.json` via `GetValue<string?>("CertificatePassword")`. Configuration files on disk are readable by any local user. The certificate password should be stored in the platform credential store (DPAPI/Keychain/libsecret) which the application already uses for other secrets.
- **Evidence:**
  - `Program.cs:65` — `var wsCertPassword = wsConfig.GetValue<string?>("CertificatePassword")`
  - `Program.cs:80` — passed directly to `listenOptions.UseHttps(wsCertPath, wsCertPassword ?? "")`
- **Impact:** Any local user who can read the appsettings.json file can extract the TLS private key password, potentially enabling MitM attacks on the WebSocket endpoint.
- **Recommended Fix:** Read the certificate password from `ICredentialStore` (already available in the DI container) or use certificate store thumbprint-based selection instead of file+password.

---

### S-DSK-003
- **Title:** No HTTPS enforcement on auto-update URL
- **Module:** Application Shell
- **Severity:** High
- **Category:** Weak authentication flows
- **Description:** `VelopackUpdateService` accepts any URL from configuration for update checks and downloads. There is no validation that the URL uses HTTPS. An HTTP update URL would allow network attackers to serve malicious update packages via MitM, resulting in arbitrary code execution on the agent device.
- **Evidence:**
  - `VelopackUpdateService.cs:65` — `new SimpleWebSource(cfg.UpdateUrl)` — no scheme validation
  - `VelopackUpdateService.cs:87` — `mgr.DownloadUpdatesAsync(updateInfo)` — downloads and stages from unchecked URL
- **Impact:** Remote code execution via MitM if a non-HTTPS update URL is configured (misconfiguration or config tamper).
- **Recommended Fix:** Add a guard: `if (!cfg.UpdateUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return error`. Also consider update package signature verification.

---

### S-DSK-004
- **Title:** Provisioning token retained in memory as plaintext string after use
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Sensitive data handling
- **Description:** The provisioning token (a one-time secret) is read from the `TokenBox.Text` control, stored in a local `string` variable, embedded in the `DeviceInfoProvider.BuildRequest()` result, and passed through the registration flow. After registration completes (success or failure), the token string remains in managed heap memory until garbage collected. In a long-running agent process, this leaves the secret accessible to memory dumps.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:178` — `var token = TokenBox.Text?.Trim()` — plaintext capture
  - `ProvisioningWindow.axaml.cs:214-216` — `DeviceInfoProvider.BuildRequest(provisioningToken: token, ...)` — embedded in request object
  - No `token = null` or `TokenBox.Text = ""` after registration completes
- **Impact:** Provisioning tokens could be extracted from process memory dumps. Limited risk since tokens are one-time-use, but violates defense-in-depth.
- **Recommended Fix:** Clear `TokenBox.Text = ""` and null out local references after registration completes. Consider using `SecureString` or a pinned buffer for the token if the platform supports it.

---

### S-DSK-005
- **Title:** Certificate password logged in debug context via Serilog structured logging
- **Module:** Application Shell
- **Severity:** Medium
- **Category:** Sensitive data written to logs
- **Description:** While the certificate path is explicitly logged (`Log.Information("WebSocket TLS endpoint configured on port {Port} with certificate {Cert}", wsPort, wsCertPath)`), the password variable `wsCertPassword` is defined in the same scope and could be inadvertently added to a log statement during debugging. More critically, the `SensitiveDataDestructuringPolicy` only redacts properties marked with `[SensitiveData]` — the certificate password is a local variable, not a property, so it has no protection from accidental logging.
- **Evidence:**
  - `Program.cs:65` — `wsCertPassword` in scope alongside log statements
  - `Program.cs:81` — `Log.Information(...)` logs cert path but not password (currently safe, but fragile)
- **Impact:** Low immediate risk, but the proximity of the secret to log statements makes accidental exposure likely during future code changes.
- **Recommended Fix:** Move certificate configuration into a dedicated method, keep the password variable scoped as tightly as possible, and add a code comment warning against logging it.

---

## Device Provisioning Module

### S-DSK-006
- **Title:** Manual offline provisioning bypasses HTTPS enforcement — cloud sync can use plaintext HTTP
- **Module:** Device Provisioning
- **Severity:** High
- **Category:** Weak authentication flows
- **Description:** When using manual configuration without a provisioning token (offline mode), `ValidateManualConfigAsync()` validates the cloud URL with `Uri.TryCreate` checking for `http` or `https` scheme — it explicitly accepts HTTP URLs. Since no cloud registration occurs (offline path), `DeviceRegistrationService.RegisterAsync()` is never called, which means `CloudUrlGuard.IsSecure()` never runs. The HTTP URL is stored in `RegistrationState.CloudBaseUrl` and propagated to `AgentConfiguration.CloudBaseUrl` via `RegistrationManager.PostConfigure()`. All cloud workers (CloudUploadWorker, ConfigPollWorker, TelemetryReporter) use this base URL for API calls via the "cloud" named HttpClient. While the HttpClient handler enforces TLS 1.2+, this only applies when the URL scheme triggers TLS negotiation — an HTTP URL bypasses TLS entirely. Transaction data, telemetry, and device tokens could be sent in plaintext.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:322-323` — `(uri.Scheme != "http" && uri.Scheme != "https")` — HTTP explicitly accepted
  - `ProvisioningWindow.axaml.cs:344-346` — offline path skips `DeviceRegistrationService.RegisterAsync` and `CloudUrlGuard.IsSecure`
  - `RegistrationManager.cs:197-198` — `options.CloudBaseUrl = state.CloudBaseUrl` — unvalidated URL propagated
  - `ServiceCollectionExtensions.cs:81` — `SslProtocols = Tls12 | Tls13` — only applies when URL triggers TLS
- **Impact:** Financial transaction data, device identity, and authentication tokens transmitted in cleartext over the network. Attacker on the same network can intercept and modify data.
- **Recommended Fix:** Add `CloudUrlGuard.IsSecure()` validation in `ValidateManualConfigAsync()` before accepting the cloud URL. Reject non-HTTPS URLs with a clear error message. Alternatively, enforce HTTPS in `RegistrationManager.PostConfigure()` as a defense-in-depth check.

---

### S-DSK-007
- **Title:** Cloud connection test falls back to raw HttpClient without TLS enforcement or certificate pinning
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Weak authentication flows
- **Description:** In `RunConnectionTestsAsync()`, the cloud health check creates the HTTP client via `_httpClientFactory?.CreateClient("cloud") ?? new HttpClient()`. If `_httpClientFactory` is null (which is possible since it's resolved from `AgentAppContext.ServiceProvider` via nullable `GetService<T>()`), a plain `HttpClient` without TLS enforcement, certificate pinning, or any custom handler configuration is used. This fallback client sends a GET request to the cloud URL without transport security validation. A MitM attacker could intercept the request and return a fake 200 OK, causing the connectivity test to pass and the user to proceed with a compromised network path.
- **Evidence:**
  - `ProvisioningWindow.axaml.cs:473-474` — `using var httpClient = _httpClientFactory?.CreateClient("cloud") ?? new HttpClient()` — nullable fallback
  - `ProvisioningWindow.axaml.cs:19` — `private readonly IHttpClientFactory? _httpClientFactory` — nullable field
  - `ProvisioningWindow.axaml.cs:51` — `_httpClientFactory = services?.GetService<IHttpClientFactory>()` — can be null if ServiceProvider is null
  - `ServiceCollectionExtensions.cs:74-84` — "cloud" client configured with TLS 1.2+, cert pinning — bypassed by fallback
- **Impact:** False-positive connectivity test result under MitM conditions. No credentials are leaked in this specific request (health check only), but it gives false confidence that the cloud connection is secure when it may not be.
- **Recommended Fix:** Throw or show an error if `_httpClientFactory` is null instead of falling back to a raw client. The health check should use the same transport security as production cloud calls. Alternatively, skip the test entirely if the factory is unavailable.

---

### S-DSK-008
- **Title:** Linux credential store fallback encryption key derivable from same-directory files
- **Module:** Device Provisioning
- **Severity:** Medium
- **Category:** Insecure storage of credentials
- **Description:** On Linux systems without `secret-tool` (libsecret), `PlatformCredentialStore` falls back to AES-256 file-based encryption. The encryption key is derived via PBKDF2 from `/etc/machine-id` (world-readable) combined with a per-installation random salt. Both the salt file (`secrets/installation.salt`) and the encrypted credentials (`secrets/credentials.dat`) are stored in the same agent data directory. An attacker with read access to this directory can derive the encryption key by reading `installation.salt`, reading `/etc/machine-id`, and running the same PBKDF2 derivation. While `SetRestrictivePermissions` is applied to the `secrets/` directory, file permission enforcement depends on the filesystem and may not be effective on shared volumes, Docker bind mounts, or backup media.
- **Evidence:**
  - `PlatformCredentialStore.cs:324-330` — `DeriveLinuxMachineKey()` uses `/etc/machine-id` (world-readable on most Linux distros)
  - `PlatformCredentialStore.cs:350-355` — salt stored at `{AgentDataDirectory}/secrets/installation.salt` alongside credentials
  - `PlatformCredentialStore.cs:416` — credentials at `{AgentDataDirectory}/secrets/credentials.dat` in the same directory
  - `PlatformCredentialStore.cs:338-343` — PBKDF2 derivation with 100K iterations — reproducible with known inputs
- **Impact:** Device JWT, refresh tokens, and API keys can be decrypted by any process that can read the agent data directory on Linux. This includes backup tools, container volume mounts, and other users on multi-tenant systems.
- **Recommended Fix:** Consider using a hardware-bound secret (e.g., TPM2 via `tpm2-pkcs11`) as an additional key derivation input. Alternatively, store the salt in a separate location from the encrypted data (e.g., user keyring, environment variable, or a different filesystem path with different ACLs). Document the threat model limitation for environments where libsecret is unavailable.
