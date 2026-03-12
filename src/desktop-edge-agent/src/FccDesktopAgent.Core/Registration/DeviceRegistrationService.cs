using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Sync;
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
    private readonly ILogger<DeviceRegistrationService> _logger;

    public DeviceRegistrationService(
        IHttpClientFactory httpFactory,
        IDeviceTokenProvider tokenProvider,
        IRegistrationManager registrationManager,
        IConfigManager configManager,
        ILogger<DeviceRegistrationService> logger)
    {
        _httpFactory = httpFactory;
        _tokenProvider = tokenProvider;
        _registrationManager = registrationManager;
        _configManager = configManager;
        _logger = logger;
    }

    public async Task<RegistrationResult> RegisterAsync(
        string cloudBaseUrl, DeviceRegistrationRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cloudBaseUrl);

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

        if (result is null || string.IsNullOrEmpty(result.DeviceId))
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
            DeviceId = result.DeviceId,
            SiteCode = result.SiteCode,
            LegalEntityId = result.LegalEntityId,
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
            await _configManager.ApplyConfigAsync(result.SiteConfig, configJson, configVersion, ct);
            _logger.LogInformation("Bootstrap site config applied (version {Version})", configVersion);
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
            var errorBody = await response.Content.ReadFromJsonAsync<RegistrationErrorResponse>(JsonOptions, ct);
            errorCode = errorBody?.ErrorCode ?? "UNKNOWN";
            message = errorBody?.Message ?? $"HTTP {(int)response.StatusCode}";
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
