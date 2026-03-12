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
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly ILogger<VersionCheckService> _logger;

    public VersionCheckService(
        IHttpClientFactory httpFactory,
        IDeviceTokenProvider tokenProvider,
        IOptions<AgentConfiguration> config,
        ILogger<VersionCheckService> logger)
    {
        _httpFactory = httpFactory;
        _tokenProvider = tokenProvider;
        _config = config;
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

        var token = await _tokenProvider.GetTokenAsync(ct);
        if (token is null)
        {
            _logger.LogWarning("Version check skipped: no device token available (not yet registered)");
            return null;
        }

        var agentVersion = GetAgentVersion();
        _logger.LogInformation("Performing startup version check (agentVersion={Version})", agentVersion);

        var result = await CallVersionCheckAsync(agentVersion, token, config, ct);

        // Retry once on 401 with token refresh
        if (result is null)
        {
            return null;
        }

        return result;
    }

    private async Task<VersionCheckResult?> CallVersionCheckAsync(
        string agentVersion,
        string token,
        AgentConfiguration config,
        CancellationToken ct)
    {
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{VersionCheckPath}?agentVersion={Uri.EscapeDataString(agentVersion)}";

        try
        {
            var http = _httpFactory.CreateClient("cloud");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Try token refresh once
                _logger.LogDebug("Version check received 401; refreshing token and retrying");
                var refreshedToken = await _tokenProvider.RefreshTokenAsync(ct);
                if (refreshedToken is null)
                {
                    _logger.LogWarning("Version check skipped: token refresh failed — allowing FCC (fail-open)");
                    return null;
                }

                using var retryRequest = new HttpRequestMessage(HttpMethod.Get, url);
                retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
                response = await http.SendAsync(retryRequest, ct);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Version check returned {StatusCode} — allowing FCC (fail-open)", response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<VersionCheckApiResponse>(ct);
            if (body is null)
            {
                _logger.LogWarning("Version check returned empty body — allowing FCC (fail-open)");
                return null;
            }

            return new VersionCheckResult
            {
                Compatible = body.Compatible,
                MinimumVersion = body.MinimumVersion,
                LatestVersion = body.LatestVersion,
                UpdateRequired = body.UpdateRequired,
                UpdateUrl = body.UpdateUrl,
                UpdateAvailable = body.UpdateAvailable,
                ReleaseNotes = body.ReleaseNotes,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Version check failed — allowing FCC (fail-open)");
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

    // Internal DTO matching the cloud API response shape
    private sealed class VersionCheckApiResponse
    {
        public bool Compatible { get; set; }
        public string MinimumVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public bool UpdateRequired { get; set; }
        public string? UpdateUrl { get; set; }
        public string AgentVersion { get; set; } = string.Empty;
        public bool UpdateAvailable { get; set; }
        public string? ReleaseNotes { get; set; }
    }
}
