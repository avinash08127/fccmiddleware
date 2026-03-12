using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Polls <c>GET /api/v1/agent/config</c> with <c>If-None-Match</c> ETag support
/// and delegates config application to <see cref="IConfigManager"/>.
///
/// Implements <see cref="IConfigPoller"/> — called by <see cref="Runtime.CadenceController"/>
/// on each internet-up tick at the config poll interval (architecture rule #10: no independent timer loop).
///
/// Architecture guarantees:
/// - 304 Not Modified is handled efficiently (no re-parse, no DB write).
/// - Config version tracked in <see cref="IConfigManager.CurrentConfigVersion"/>.
/// - Decommission state is permanent for the process lifetime; restart required to re-enable.
/// </summary>
public sealed class ConfigPollWorker : IConfigPoller
{
    private const string ConfigPath = "/api/v1/agent/config";
    private const string DecommissionedCode = "DEVICE_DECOMMISSIONED";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IConfigManager _configManager;
    private readonly IRegistrationManager _registrationManager;
    private readonly ILogger<ConfigPollWorker> _logger;

    // Set permanently on DEVICE_DECOMMISSIONED. Process restart required to clear.
    private volatile bool _decommissioned;

    public ConfigPollWorker(
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        IDeviceTokenProvider tokenProvider,
        IConfigManager configManager,
        IRegistrationManager registrationManager,
        ILogger<ConfigPollWorker> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _tokenProvider = tokenProvider;
        _configManager = configManager;
        _registrationManager = registrationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> PollAsync(CancellationToken ct)
    {
        if (_decommissioned)
        {
            _logger.LogDebug("Config poll skipped: device is decommissioned");
            return false;
        }

        var token = await _tokenProvider.GetTokenAsync(ct);
        if (token is null)
        {
            _logger.LogWarning("Config poll skipped: no device token available (device not yet registered?)");
            return false;
        }

        ConfigPollResponse? response;
        try
        {
            response = await SendPollRequestAsync(token, ct);
        }
        catch (UnauthorizedAccessException)
        {
            // 401: refresh token once and retry
            _logger.LogWarning("Config poll received 401; refreshing device token");
            token = await _tokenProvider.RefreshTokenAsync(ct);
            if (token is null)
            {
                _logger.LogWarning("Token refresh failed; config poll aborted");
                return false;
            }

            try
            {
                response = await SendPollRequestAsync(token, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Config poll failed after token refresh");
                return false;
            }
        }
        catch (DeviceDecommissionedException ex)
        {
            await _registrationManager.MarkDecommissionedAsync();
            _decommissioned = true;
            _logger.LogCritical(
                "DEVICE_DECOMMISSIONED received during config poll. " +
                "All cloud sync halted. Agent restart required. Reason: {Reason}", ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Config poll HTTP request failed");
            return false;
        }

        // null = 304 Not Modified
        if (response is null)
            return false;

        // Apply the new config
        var result = await _configManager.ApplyConfigAsync(
            response.Config, response.RawJson, response.ConfigVersion, ct);

        if (result.Outcome == ConfigApplyOutcome.Applied)
        {
            _logger.LogInformation(
                "Config version {Version} applied successfully", result.ConfigVersion);
            return true;
        }

        _logger.LogDebug(
            "Config version {Version} not applied: {Outcome}",
            result.ConfigVersion, result.Outcome);
        return false;
    }

    // ── HTTP ─────────────────────────────────────────────────────────────────

    private async Task<ConfigPollResponse?> SendPollRequestAsync(string token, CancellationToken ct)
    {
        var config = _config.Value;
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{ConfigPath}";

        var http = _httpFactory.CreateClient("cloud");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Include current config version as ETag for conditional GET
        var currentVersion = _configManager.CurrentConfigVersion;
        if (currentVersion is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{currentVersion}\"");

        var response = await http.SendAsync(request, ct);

        // 304 Not Modified — config unchanged, no re-parse needed
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _logger.LogDebug("Config poll: 304 Not Modified (version {Version})", currentVersion);
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains(DecommissionedCode, StringComparison.OrdinalIgnoreCase))
                throw new DeviceDecommissionedException(body);
            throw new HttpRequestException($"403 Forbidden: {body}");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogError(
                "Config poll: 404 — no config found for this device (re-registration may be required)");
            return null;
        }

        response.EnsureSuccessStatusCode();

        // Read raw JSON for database storage, then deserialize
        var rawJson = await response.Content.ReadAsStringAsync(ct);
        var siteConfig = JsonSerializer.Deserialize<SiteConfig>(rawJson, JsonOptions);

        if (siteConfig is null)
        {
            _logger.LogWarning("Config poll: failed to deserialize response body");
            return null;
        }

        // Extract config version from ETag header or from the config body
        var etag = response.Headers.ETag?.Tag?.Trim('"');
        var configVersion = etag ?? siteConfig.ConfigVersion.ToString();

        return new ConfigPollResponse(siteConfig, rawJson, configVersion);
    }
}

internal sealed record ConfigPollResponse(SiteConfig Config, string RawJson, string ConfigVersion);
