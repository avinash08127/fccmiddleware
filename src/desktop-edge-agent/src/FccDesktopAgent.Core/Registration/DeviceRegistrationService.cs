using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Peer;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync;
using FccMiddleware.Contracts.Common;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Registration;

/// <summary>
/// Handles the device registration HTTP call and coordinates post-registration side effects:
/// token storage, identity persistence, and bootstrap config application.
/// </summary>
public sealed class DeviceRegistrationService : IDeviceRegistrationService
{
    private const string RegisterPath = "/api/v1/agent/register";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IRegistrationManager _registrationManager;
    private readonly IConfigManager _configManager;
    private readonly LanPeerAnnouncer? _lanPeerAnnouncer;
    private readonly ILogger<DeviceRegistrationService> _logger;

    public DeviceRegistrationService(
        IHttpClientFactory httpFactory,
        IDeviceTokenProvider tokenProvider,
        IRegistrationManager registrationManager,
        IConfigManager configManager,
        ILogger<DeviceRegistrationService> logger,
        LanPeerAnnouncer? lanPeerAnnouncer = null)
    {
        _httpFactory = httpFactory;
        _tokenProvider = tokenProvider;
        _registrationManager = registrationManager;
        _configManager = configManager;
        _lanPeerAnnouncer = lanPeerAnnouncer;
        _logger = logger;
    }

    public async Task<RegistrationResult> RegisterAsync(
        string cloudBaseUrl, DeviceRegistrationRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cloudBaseUrl);

        // H-09: Enforce HTTPS to prevent provisioning token and device tokens
        // from being transmitted in cleartext. Allows HTTP only for localhost.
        if (!CloudUrlGuard.IsSecure(cloudBaseUrl))
        {
            _logger.LogWarning("Registration rejected — cloud URL must use HTTPS: {Url}", cloudBaseUrl);
            return new RegistrationResult.Rejected(
                RegistrationErrorCode.Unknown,
                "Cloud URL must use HTTPS. HTTP is only allowed for localhost development.");
        }

        var url = $"{cloudBaseUrl.TrimEnd('/')}{RegisterPath}";
        _logger.LogInformation("Registering device at {Url} for site {SiteCode}", url, request.SiteCode);

        HttpResponseMessage response;
        try
        {
            var http = _httpFactory.CreateClient("cloud");
            response = await http.PostAsJsonAsync(url, request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Registration HTTP call failed");
            return new RegistrationResult.TransportError(
                $"Network error: {ex.Message}", ex);
        }

        // Success (201 Created)
        if (response.StatusCode == HttpStatusCode.Created)
        {
            return await HandleSuccessAsync(cloudBaseUrl, request, response, ct);
        }

        // Client error (4xx) — parse error response
        if ((int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
        {
            return await HandleRejectionAsync(response, ct);
        }

        // Other status codes
        _logger.LogWarning("Registration returned unexpected status {StatusCode}", response.StatusCode);
        return new RegistrationResult.TransportError(
            $"Unexpected HTTP {(int)response.StatusCode} from registration endpoint");
    }

    private async Task<RegistrationResult> HandleSuccessAsync(
        string cloudBaseUrl, DeviceRegistrationRequest request,
        HttpResponseMessage response, CancellationToken ct)
    {
        DeviceRegistrationResponse? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(JsonOptions, ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize registration response");
            return new RegistrationResult.TransportError("Invalid registration response format");
        }

        if (result is null || result.DeviceId == Guid.Empty)
        {
            return new RegistrationResult.TransportError("Registration response missing required fields");
        }

        // 1. Store tokens in credential store
        await _tokenProvider.StoreTokensAsync(result.DeviceToken, result.RefreshToken, ct);
        _logger.LogInformation("Device tokens stored securely");

        // 2. Persist registration identity
        var state = new RegistrationState
        {
            IsRegistered = true,
            IsDecommissioned = false,
            DeviceId = result.DeviceId.ToString(),
            SiteCode = result.SiteCode,
            LegalEntityId = result.LegalEntityId.ToString(),
            CloudBaseUrl = cloudBaseUrl,
            RegisteredAt = result.RegisteredAt,
            DeviceSerialNumber = request.DeviceSerialNumber,
            DeviceModel = request.DeviceModel,
            OsVersion = request.OsVersion,
            AgentVersion = request.AgentVersion,
        };
        await _registrationManager.SaveStateAsync(state, ct);

        // 3. Apply bootstrap SiteConfig if provided
        if (result.SiteConfig is not null)
        {
            var configJson = JsonSerializer.Serialize(result.SiteConfig);
            var configVersion = result.SiteConfig.ConfigVersion > 0
                ? result.SiteConfig.ConfigVersion.ToString()
                : "bootstrap-1";
            var applyResult = await _configManager.ApplyConfigAsync(result.SiteConfig, configJson, configVersion, ct);
            if (applyResult.Outcome != ConfigApplyOutcome.Applied)
            {
                var message = applyResult.Outcome switch
                {
                    ConfigApplyOutcome.Rejected => applyResult.ErrorMessage ?? "Bootstrap site config failed validation.",
                    ConfigApplyOutcome.NotYetEffective => "Bootstrap site config is not yet effective.",
                    ConfigApplyOutcome.StaleVersion => "Bootstrap site config version is stale.",
                    _ => "Bootstrap site config could not be applied."
                };

                _logger.LogError(
                    "Registration succeeded but bootstrap config was not applied (outcome={Outcome}, version={Version}): {Message}",
                    applyResult.Outcome,
                    configVersion,
                    message);

                return new RegistrationResult.TransportError(
                    $"Registration succeeded but bootstrap config was not applied: {message}");
            }

            _logger.LogInformation("Bootstrap site config applied (version {Version})", configVersion);

            // M-09: Sync equipment data immediately so the local API has pump/nozzle
            // info before the first config poll (potentially 60+ seconds away).
            try
            {
                await _registrationManager.SyncSiteDataAsync(result.SiteConfig);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Site data sync failed after registration — will populate on first config poll");
            }
        }

        // P2-12: Broadcast UDP peer announcement after registration so LAN peers discover us
        try
        {
            _lanPeerAnnouncer?.Broadcast();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LAN peer announcement after registration failed");
        }

        _logger.LogInformation(
            "Device registered successfully (deviceId={DeviceId}, site={SiteCode})",
            result.DeviceId, result.SiteCode);

        return new RegistrationResult.Success(result);
    }

    private async Task<RegistrationResult> HandleRejectionAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        string errorCode;
        string message;

        try
        {
            var errorBody = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, ct);
            errorCode = errorBody.GetErrorCode();
            message = errorBody.GetMessage((int)response.StatusCode);
        }
        catch
        {
            errorCode = "UNKNOWN";
            message = $"HTTP {(int)response.StatusCode} (could not parse error body)";
        }

        var code = RegistrationErrorCodeParser.Parse(errorCode);

        _logger.LogWarning("Registration rejected: {ErrorCode} — {Message}", errorCode, message);

        return new RegistrationResult.Rejected(code, message);
    }
}
