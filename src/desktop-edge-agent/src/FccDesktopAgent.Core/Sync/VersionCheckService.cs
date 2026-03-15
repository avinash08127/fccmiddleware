using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Checks agent version compatibility by calling GET /api/v1/agent/version-check.
/// Per requirements §15.13: agent calls this on startup and disables FCC
/// communication if below minimum supported version.
/// </summary>
public sealed class VersionCheckService : IVersionChecker
{
    private const string VersionCheckPath = "/api/v1/agent/version-check";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly AuthenticatedCloudRequestHandler _authHandler;
    private readonly IConfigManager _configManager;
    private readonly ILogger<VersionCheckService> _logger;

    public VersionCheckService(
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        AuthenticatedCloudRequestHandler authHandler,
        IConfigManager configManager,
        ILogger<VersionCheckService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _authHandler = authHandler;
        _configManager = configManager;
        _logger = logger;
    }

    public async Task<VersionCheckResult?> CheckVersionAsync(CancellationToken ct = default)
    {
        var config = _config.Value;
        if (string.IsNullOrWhiteSpace(config.CloudBaseUrl))
        {
            _logger.LogWarning("Version check skipped: CloudBaseUrl not configured");
            return null;
        }

        var agentVersion = GetAgentVersion();
        _logger.LogInformation("Performing startup version check (agentVersion={Version})", agentVersion);

        var result = await _authHandler.ExecuteAsync<VersionCheckApiResponse?>(
            (token, innerCt) => SendVersionCheckAsync(agentVersion, token, config, innerCt),
            "version check",
            ct);

        if (result.Outcome == AuthRequestOutcome.NoToken)
        {
            _logger.LogWarning("Version check skipped: no device token available (not yet registered)");
            return null;
        }

        if (result.RequiresHalt)
            return null;

        if (result.Outcome == AuthRequestOutcome.AuthFailed)
        {
            _logger.LogWarning("Version check skipped: token refresh failed");
            return null;
        }

        if (!result.IsSuccess)
        {
            _logger.LogWarning(result.Error, "Version check failed — allowing FCC (fail-open)");
            return null;
        }

        var response = result.Value;
        if (response is null)
        {
            _logger.LogWarning("Version check returned empty body — allowing FCC (fail-open)");
            return null;
        }

        return new VersionCheckResult
        {
            Compatible = response.Compatible,
            MinimumVersion = response.MinimumVersion,
            LatestVersion = response.LatestVersion,
            UpdateRequired = response.UpdateRequired,
            UpdateUrl = response.UpdateUrl,
            UpdateAvailable = response.UpdateAvailable,
            ReleaseNotes = response.ReleaseNotes,
        };
    }

    private async Task<VersionCheckApiResponse?> SendVersionCheckAsync(
        string agentVersion,
        string token,
        AgentConfiguration config,
        CancellationToken ct)
    {
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{VersionCheckPath}?agentVersion={Uri.EscapeDataString(agentVersion)}";

        var http = _httpFactory.CreateClient("cloud");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);

        PeerDirectoryVersionHelper.CheckAndTrigger(response, _configManager, _logger);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var error = await TryReadErrorAsync(response, ct);
            if (string.Equals(error?.ErrorCode, "DEVICE_DECOMMISSIONED", StringComparison.OrdinalIgnoreCase))
                throw new DeviceDecommissionedException(error?.Message ?? "Device decommissioned");

            throw new HttpRequestException(error?.Message ?? "403 Forbidden");
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadErrorAsync(response, ct);
            throw new HttpRequestException(error?.Message ?? $"Version check failed with {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<VersionCheckApiResponse>(cancellationToken: ct);
    }

    private static async Task<ErrorResponse?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    private static string GetAgentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is not null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";
    }
}
