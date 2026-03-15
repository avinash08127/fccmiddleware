using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private readonly IConfigManager _configManager;
    private readonly AuthenticatedCloudRequestHandler _authHandler;
    private readonly IRegistrationManager _registrationManager;
    private readonly ILogger<ConfigPollWorker> _logger;

    public ConfigPollWorker(
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        IConfigManager configManager,
        AuthenticatedCloudRequestHandler authHandler,
        IRegistrationManager registrationManager,
        ILogger<ConfigPollWorker> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _configManager = configManager;
        _authHandler = authHandler;
        _registrationManager = registrationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> PollAsync(CancellationToken ct)
        => await PollWithDetailsAsync(ct) is ConfigPollExecutionResult.Applied;

    public async Task<ConfigPollExecutionResult> PollWithDetailsAsync(CancellationToken ct)
    {
        // T-DSK-013: Check centralized decommission flag instead of per-worker volatile boolean.
        if (_registrationManager.IsDecommissioned)
        {
            _logger.LogDebug("Config poll skipped: device is decommissioned");
            return new ConfigPollExecutionResult.Decommissioned();
        }

        // T-DSK-010: Delegate auth flow (token acquisition, 401 refresh, decommission handling)
        // to the shared handler — eliminates duplicated try/catch pattern.
        var result = await _authHandler.ExecuteAsync<ConfigPollResponse?>(
            SendPollRequestAsync, "config poll", ct);

        if (result.RequiresHalt)
            return result.Outcome == AuthRequestOutcome.Decommissioned
                ? new ConfigPollExecutionResult.Decommissioned()
                : new ConfigPollExecutionResult.Unavailable("re-provisioning required");

        if (result.Outcome == AuthRequestOutcome.NoToken)
            return new ConfigPollExecutionResult.Unavailable("no device token available");

        if (result.Outcome == AuthRequestOutcome.AuthFailed)
            return new ConfigPollExecutionResult.TransportFailure("token refresh failed");

        if (!result.IsSuccess)
            return new ConfigPollExecutionResult.TransportFailure(result.Error?.Message ?? "config poll failed");

        var response = result.Value;
        if (response is null)
            return new ConfigPollExecutionResult.TransportFailure("config poll failed");

        if (response.Outcome == ConfigPollHttpOutcome.NotModified)
            return new ConfigPollExecutionResult.Unchanged(ParseCurrentConfigVersion());

        if (response.Outcome == ConfigPollHttpOutcome.NotFound)
        {
            return new ConfigPollExecutionResult.Unavailable(
                response.Message ?? "cloud site config is not available for this device");
        }

        // Apply the new config
        var applyResult = await _configManager.ApplyConfigAsync(
            response.Config!, response.RawJson!, response.ConfigVersion!, ct);

        return applyResult.Outcome switch
        {
            ConfigApplyOutcome.Applied => BuildAppliedResult(applyResult.ConfigVersion),
            ConfigApplyOutcome.StaleVersion or ConfigApplyOutcome.NotYetEffective => new ConfigPollExecutionResult.Skipped(applyResult.ConfigVersion),
            ConfigApplyOutcome.Rejected => new ConfigPollExecutionResult.Rejected(
                applyResult.ConfigVersion,
                applyResult.ErrorMessage ?? "Config validation failed"),
            _ => new ConfigPollExecutionResult.TransportFailure("config poll failed")
        };
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

        PeerDirectoryVersionHelper.CheckAndTrigger(response, _configManager, _logger);

        // 304 Not Modified — config unchanged, no re-parse needed
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _logger.LogDebug("Config poll: 304 Not Modified (version {Version})", currentVersion);
            return ConfigPollResponse.NotModified();
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
            var error = await TryReadErrorAsync(response, ct);
            var message = error?.Message ?? "Cloud config is missing for this device.";
            _logger.LogError(
                "Config poll: 404 {ErrorCode} — {Message}",
                error?.ErrorCode ?? "NOT_FOUND",
                message);
            return ConfigPollResponse.NotFound(error?.ErrorCode, message);
        }

        response.EnsureSuccessStatusCode();

        // Read raw JSON for database storage, then deserialize
        var rawJson = await response.Content.ReadAsStringAsync(ct);
        var siteConfig = JsonSerializer.Deserialize<SiteConfig>(rawJson, JsonOptions);

        if (siteConfig is null)
            throw new HttpRequestException("Config poll returned an empty or invalid config payload.");

        // Extract config version from ETag header or from the config body
        var etag = response.Headers.ETag?.Tag?.Trim('"');
        var configVersion = etag ?? siteConfig.ConfigVersion.ToString();

        return ConfigPollResponse.Success(siteConfig, rawJson, configVersion);
    }

    private ConfigPollExecutionResult.Applied BuildAppliedResult(int configVersion)
    {
        _logger.LogInformation("Config version {Version} applied successfully", configVersion);
        return new ConfigPollExecutionResult.Applied(configVersion);
    }

    private int? ParseCurrentConfigVersion()
    {
        return int.TryParse(_configManager.CurrentConfigVersion, out var version) ? version : null;
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
}

internal enum ConfigPollHttpOutcome
{
    Success,
    NotModified,
    NotFound,
}

internal sealed record ConfigPollResponse(
    ConfigPollHttpOutcome Outcome,
    SiteConfig? Config = null,
    string? RawJson = null,
    string? ConfigVersion = null,
    string? ErrorCode = null,
    string? Message = null)
{
    public static ConfigPollResponse Success(SiteConfig config, string rawJson, string configVersion) =>
        new(ConfigPollHttpOutcome.Success, config, rawJson, configVersion);

    public static ConfigPollResponse NotModified() =>
        new(ConfigPollHttpOutcome.NotModified);

    public static ConfigPollResponse NotFound(string? errorCode, string? message) =>
        new(ConfigPollHttpOutcome.NotFound, ErrorCode: errorCode, Message: message);
}
