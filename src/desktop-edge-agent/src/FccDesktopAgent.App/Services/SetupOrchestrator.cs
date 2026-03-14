using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.App.Services;

// ── Result Types ─────────────────────────────────────────────────────────────

public enum RegistrationOutcomeKind { Success, Rejected, TransportError, TimedOut, Error }

public record RegistrationOutcome(
    RegistrationOutcomeKind Kind,
    string? ErrorMessage = null,
    string? ErrorHint = null);

public record ValidationOutcome(bool IsValid, string? ErrorMessage = null);

public record ConnectionTestResults(TestOutcome Cloud, TestOutcome Fcc)
{
    public bool AllPassed => Cloud.Ok && Fcc.Ok;
    public bool CloudOnlyPassed => Cloud.Ok && !Fcc.Ok;
}

public record TestOutcome(bool Ok, TestState State, string Detail);

public enum TestState { Connected, Warning, Failed, Skipped }

/// <summary>
/// Encapsulates the business logic of the provisioning wizard: cloud registration,
/// manual config validation, connection testing, and host startup.
/// Extracted from ProvisioningWindow code-behind (T-DSK-003).
/// </summary>
public sealed class SetupOrchestrator
{
    private readonly IDeviceRegistrationService? _registrationService;
    private readonly IRegistrationManager? _registrationManager;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ICredentialStore? _credentialStore;
    private readonly IAgentCommandStateStore? _commandStateStore;
    private readonly ILogger<SetupOrchestrator>? _logger;
    private readonly AgentConfiguration? _agentConfig;

    public SetupOrchestrator(IServiceProvider? services)
    {
        _registrationService = services?.GetService<IDeviceRegistrationService>();
        _registrationManager = services?.GetService<IRegistrationManager>();
        _httpClientFactory = services?.GetService<IHttpClientFactory>();
        _credentialStore = services?.GetService<ICredentialStore>();
        _commandStateStore = services?.GetService<IAgentCommandStateStore>();
        _logger = services?.GetService<ILogger<SetupOrchestrator>>();
        _agentConfig = services?.GetService<IOptions<AgentConfiguration>>()?.Value;
    }

    // ── Provisioning State ───────────────────────────────────────────────────

    public string CloudUrl { get; private set; } = string.Empty;
    public string SiteCode { get; private set; } = string.Empty;
    public string FccHost { get; private set; } = string.Empty;
    public int FccPort { get; private set; }
    public string DeviceId { get; private set; } = string.Empty;
    public string Environment { get; private set; } = string.Empty;
    public string ApiKey { get; private set; } = string.Empty;
    public bool CloudRegistrationDone { get; private set; }

    /// <summary>
    /// Tracks whether the registration service already persisted state.
    /// Replaces the fragile _isCodeMethod flag mutation (T-DSK-009 side-fix).
    /// </summary>
    public bool StateAlreadyPersisted { get; private set; }

    // ── Code-Based Registration ──────────────────────────────────────────────

    public async Task<RegistrationOutcome> RegisterWithCodeAsync(
        string? cloudUrl, string? siteCode, string? token,
        string? environment, bool replaceExisting,
        CancellationToken ct)
    {
        cloudUrl = cloudUrl?.Trim();
        siteCode = siteCode?.Trim();
        token = token?.Trim();

        if (string.IsNullOrWhiteSpace(cloudUrl))
            return new(RegistrationOutcomeKind.Error, "Please enter the cloud URL.");
        // S-DSK-006: Enforce HTTPS for cloud URLs. HTTP only allowed for localhost.
        if (!CloudUrlGuard.IsSecure(cloudUrl))
            return new(RegistrationOutcomeKind.Error, "Cloud URL must use HTTPS. HTTP is only allowed for localhost.");
        if (string.IsNullOrWhiteSpace(siteCode))
            return new(RegistrationOutcomeKind.Error, "Please enter the site code.");
        if (string.IsNullOrWhiteSpace(token))
            return new(RegistrationOutcomeKind.Error, "Please enter the provisioning token.");

        // T-DSK-006: Delegate to shared registration core.
        return await ExecuteCloudRegistrationAsync(
            cloudUrl, siteCode, token, replaceExisting, environment,
            fallbackFccHost: string.Empty, fallbackFccPort: 8080, ct);
    }

    // ── Manual Config Validation ─────────────────────────────────────────────

    public ValidationOutcome ValidateManualConfig(
        string? cloudUrl, string? siteCode, string? fccHost,
        string? fccPortText, string? environment)
    {
        if (string.IsNullOrWhiteSpace(cloudUrl))
            return new(false, "Please enter the cloud URL.");
        if (string.IsNullOrWhiteSpace(siteCode))
            return new(false, "Please enter the site code.");
        if (string.IsNullOrWhiteSpace(fccHost))
            return new(false, "Please enter the FCC host address.");
        if (!int.TryParse(fccPortText, out var fccPort) || fccPort < 1 || fccPort > 65535)
            return new(false, "Please enter a valid FCC port (1-65535).");
        // S-DSK-006: Enforce HTTPS for cloud URLs. HTTP only allowed for localhost.
        if (!CloudUrlGuard.IsSecure(cloudUrl))
            return new(false, "Cloud URL must use HTTPS. HTTP is only allowed for localhost.");

        CloudUrl = cloudUrl.Trim();
        SiteCode = siteCode.Trim();
        FccHost = fccHost.Trim();
        FccPort = fccPort;
        Environment = environment ?? string.Empty;
        // F-DSK-011: Use the full GUID to preserve 128-bit collision resistance.
        DeviceId = $"manual-{Guid.NewGuid():N}";
        StateAlreadyPersisted = false;

        return new(true);
    }

    // ── Manual Config with Token ─────────────────────────────────────────────

    public async Task<RegistrationOutcome> RegisterManualWithTokenAsync(
        string cloudUrl, string siteCode, string token,
        string fccHost, int fccPort, string? environment,
        CancellationToken ct)
    {
        // T-DSK-006: Delegate to shared registration core.
        return await ExecuteCloudRegistrationAsync(
            cloudUrl, siteCode, token, replaceExisting: false, environment,
            fallbackFccHost: fccHost, fallbackFccPort: fccPort, ct);
    }

    // ── Shared Registration Core (T-DSK-006) ──────────────────────────────

    /// <summary>
    /// Shared registration logic used by both code-based and manual-token paths.
    /// Eliminates the ~200 lines of duplicated business logic (T-DSK-006).
    /// SyncSiteData is NOT called here — DeviceRegistrationService.HandleSuccessAsync
    /// already handles it (T-DSK-008).
    /// </summary>
    private async Task<RegistrationOutcome> ExecuteCloudRegistrationAsync(
        string cloudUrl, string siteCode, string token,
        bool replaceExisting, string? environment,
        string fallbackFccHost, int fallbackFccPort,
        CancellationToken ct)
    {
        if (_registrationService is null)
            return new(RegistrationOutcomeKind.Error, "Registration service unavailable.");

        try
        {
            var request = DeviceInfoProvider.BuildRequest(
                provisioningToken: token,
                siteCode: siteCode,
                replacePreviousAgent: replaceExisting);

            var result = await _registrationService.RegisterAsync(cloudUrl, request, ct);

            switch (result)
            {
                case RegistrationResult.Success success:
                    _logger?.LogInformation("Registration successful — deviceId={DeviceId}",
                        success.Response.DeviceId);

                    CloudRegistrationDone = true;
                    StateAlreadyPersisted = true;
                    CloudUrl = cloudUrl;
                    SiteCode = siteCode;
                    DeviceId = success.Response.DeviceId;
                    Environment = environment ?? string.Empty;

                    var siteConfig = success.Response.SiteConfig;
                    if (siteConfig?.Fcc is not null)
                    {
                        FccHost = siteConfig.Fcc.HostAddress ?? fallbackFccHost;
                        FccPort = siteConfig.Fcc.Port ?? fallbackFccPort;
                    }

                    if (!string.IsNullOrEmpty(environment) && _registrationManager is not null)
                    {
                        var state = _registrationManager.LoadState();
                        state.Environment = environment;
                        await _registrationManager.SaveStateAsync(state);
                    }

                    // T-DSK-008: SyncSiteData is NOT called here — DeviceRegistrationService
                    // already handles it in HandleSuccessAsync. Calling it again was redundant.

                    return new(RegistrationOutcomeKind.Success);

                case RegistrationResult.Rejected rejected:
                    return new(RegistrationOutcomeKind.Rejected, rejected.Message, GetErrorHint(rejected.Code));

                case RegistrationResult.TransportError transport:
                    return new(RegistrationOutcomeKind.TransportError, transport.Message);

                default:
                    return new(RegistrationOutcomeKind.Error, "Unknown registration result.");
            }
        }
        catch (OperationCanceledException)
        {
            return new(RegistrationOutcomeKind.TimedOut,
                "Registration timed out. Check connectivity and try again.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error during registration");
            return new(RegistrationOutcomeKind.Error, $"Unexpected error: {ex.Message}");
        }
    }

    // ── Connection Tests ─────────────────────────────────────────────────────

    public async Task<ConnectionTestResults> RunConnectionTestsAsync()
    {
        var cloud = await TestCloudConnectivityAsync();
        var fcc = await TestFccConnectivityAsync();
        return new(cloud, fcc);
    }

    private async Task<TestOutcome> TestCloudConnectivityAsync()
    {
        // M-06: Use the DI-registered "cloud" named client so the connection test
        // respects certificate pinning.
        // S-DSK-007: Do not fall back to a raw HttpClient — the "cloud" named client
        // has TLS 1.2+ enforcement and certificate pinning configured in
        // ServiceCollectionExtensions. A plain HttpClient would bypass these controls.
        try
        {
            if (_httpClientFactory is null)
                return new(false, TestState.Failed,
                    "HTTP client factory unavailable — cannot perform secure connectivity test. Restart the agent to retry.");

            using var httpClient = _httpClientFactory.CreateClient("cloud");
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var response = await httpClient.GetAsync($"{CloudUrl.TrimEnd('/')}/health");

            return response.IsSuccessStatusCode
                ? new(true, TestState.Connected, $"Connected to {CloudUrl}")
                : new(false, TestState.Warning, $"HTTP {(int)response.StatusCode} from {CloudUrl}");
        }
        catch (Exception ex)
        {
            return new(false, TestState.Failed, $"Failed: {ex.Message}");
        }
    }

    private async Task<TestOutcome> TestFccConnectivityAsync()
    {
        if (string.IsNullOrEmpty(FccHost))
            return new(true, TestState.Skipped, "No FCC host configured (will be set from cloud config)");

        // T-DSK-007: Use the DI-registered "fcc" named client instead of raw HttpClient.
        // F-DSK-010: Try HTTPS first, fall back to HTTP.
        // S-DSK-007: Do not fall back to a raw HttpClient without configured transport security.
        try
        {
            if (_httpClientFactory is null)
                return new(false, TestState.Failed,
                    "HTTP client factory unavailable — cannot perform connectivity test. Restart the agent to retry.");

            using var httpClient = _httpClientFactory.CreateClient("fcc");
            var endpoint = $"{FccHost}:{FccPort}";

            bool reached = false;
            string scheme = "https";
            try
            {
                await httpClient.GetAsync($"https://{endpoint}");
                reached = true;
            }
            catch
            {
                scheme = "http";
                try
                {
                    await httpClient.GetAsync($"http://{endpoint}");
                    reached = true;
                }
                catch { /* both failed */ }
            }

            return reached
                ? new(true, TestState.Connected, $"Reachable at {endpoint} ({scheme.ToUpperInvariant()})")
                : new(false, TestState.Failed, $"Cannot reach {endpoint} (tried HTTPS and HTTP)");
        }
        catch (Exception ex)
        {
            return new(false, TestState.Failed, $"Error: {ex.Message}");
        }
    }

    // ── Launch Helpers ───────────────────────────────────────────────────────

    public void ResolveApiKey()
    {
        ApiKey = _agentConfig?.FccApiKey ?? Guid.NewGuid().ToString("N");
    }

    public int GetLocalApiPort() => _agentConfig?.LocalApiPort ?? 8585;

    /// <summary>
    /// Persists registration state for the manual (offline) config path.
    /// Skipped when the cloud registration service already persisted state.
    /// </summary>
    public async Task PersistManualStateAsync()
    {
        if (StateAlreadyPersisted || _registrationManager is null) return;

        await _registrationManager.SaveStateAsync(new RegistrationState
        {
            IsRegistered = true,
            DeviceId = DeviceId,
            SiteCode = SiteCode,
            // L-02: Explicitly set LegalEntityId (empty for offline manual config;
            // will be populated on first cloud config poll).
            LegalEntityId = string.Empty,
            CloudBaseUrl = CloudUrl,
            Environment = string.IsNullOrEmpty(Environment) ? null : Environment,
            RegisteredAt = DateTimeOffset.UtcNow,
            DeviceModel = System.Environment.MachineName,
            OsVersion = System.Environment.OSVersion.VersionString,
            AgentVersion = typeof(SetupOrchestrator).Assembly.GetName().Version?.ToString() ?? "1.0.0",
        });
        _logger?.LogInformation(
            "Manual config registration state persisted (deviceId={DeviceId}, site={SiteCode})",
            DeviceId, SiteCode);
    }

    public async Task PersistApiKeyAsync()
    {
        if (_credentialStore is null || string.IsNullOrEmpty(ApiKey)) return;

        try
        {
            await _credentialStore.SetSecretAsync(CredentialKeys.LanApiKey, ApiKey);
            _logger?.LogInformation("LAN API key persisted to credential store");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist LAN API key to credential store");
        }
    }

    /// <summary>
    /// Starts the web host if it has not already been started.
    /// F-DSK-007: Prevents double-start during re-provisioning.
    /// </summary>
    public async Task StartHostAsync(Microsoft.AspNetCore.Builder.WebApplication? webApp, bool isHostStarted)
    {
        if (webApp is null || isHostStarted) return;
        await Task.Run(() => webApp.Start());
        _logger?.LogInformation("Host started after setup wizard");
    }

    public async Task ClearOperatorNoticeAsync()
    {
        if (_commandStateStore is null) return;
        await _commandStateStore.ClearNoticeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string GetErrorHint(RegistrationErrorCode code) => code switch
    {
        RegistrationErrorCode.BootstrapTokenExpired => "\n\nThe token has expired. Please generate a new one from the admin portal.",
        RegistrationErrorCode.BootstrapTokenAlreadyUsed => "\n\nThis token has already been used. Please generate a new one.",
        RegistrationErrorCode.BootstrapTokenInvalid => "\n\nThe token is not recognized. Please check you copied it correctly.",
        RegistrationErrorCode.BootstrapTokenRevoked => "\n\nThis token has been revoked by an administrator.",
        RegistrationErrorCode.ActiveAgentExists => "\n\nAnother agent is already registered at this site. Check 'Replace existing agent' to override.",
        RegistrationErrorCode.DevicePendingApproval => "\n\nThis device has been held for operator approval. Ask the portal operator to approve it, then retry provisioning.",
        RegistrationErrorCode.DeviceQuarantined => "\n\nThis device has been quarantined by a registration policy. Ask the portal operator to review or reject it before retrying.",
        RegistrationErrorCode.SiteNotFound => "\n\nThe site code was not found. Please check the site code.",
        RegistrationErrorCode.SiteMismatch => "\n\nThe site code does not match the token. Please verify both values.",
        _ => "",
    };
}
