using System.Net;
using System.Net.Http.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Retrieves and refreshes the device JWT used for cloud API authentication.
/// Device token stored under key <see cref="TokenKey"/>.
/// Refresh token stored under key <see cref="RefreshTokenKey"/>.
///
/// Refresh sends the refresh token in the JSON body (NOT as Bearer header) per spec.
/// Both tokens are rotated on every successful refresh (token rotation).
/// </summary>
public sealed class DeviceTokenProvider : IDeviceTokenProvider
{
    internal const string TokenKey = CredentialKeys.DeviceToken;
    internal const string RefreshTokenKey = CredentialKeys.RefreshToken;
    private const string RefreshPath = "/api/v1/agent/token/refresh";

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ICredentialStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly ILogger<DeviceTokenProvider> _logger;

    public DeviceTokenProvider(
        ICredentialStore store,
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        ILogger<DeviceTokenProvider> logger)
    {
        _store = store;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
    }

    public Task<string?> GetTokenAsync(CancellationToken ct = default)
        => _store.GetSecretAsync(TokenKey, ct);

    public async Task StoreTokensAsync(string deviceToken, string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        await _store.SetSecretAsync(TokenKey, deviceToken, ct);
        await _store.SetSecretAsync(RefreshTokenKey, refreshToken, ct);
    }

    public async Task<string?> RefreshTokenAsync(CancellationToken ct = default)
    {
        // BUG-009: Serialize refresh attempts so that concurrent 401 handlers
        // (ConfigPollWorker, CloudUploadWorker) do not race to issue duplicate
        // refresh requests — matching the Android agent's Mutex pattern.
        await _refreshLock.WaitAsync(ct);
        try
        {
            return await RefreshTokenCoreAsync(ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string?> RefreshTokenCoreAsync(CancellationToken ct)
    {
        // Per spec: send refresh token in JSON body, not as Bearer header
        var refreshToken = await _store.GetSecretAsync(RefreshTokenKey, ct);
        if (refreshToken is null)
        {
            _logger.LogWarning("Token refresh skipped: no refresh token in credential store");
            return null;
        }

        var http = _httpFactory.CreateClient("cloud");
        var config = _config.Value;

        if (string.IsNullOrWhiteSpace(config.CloudBaseUrl))
        {
            _logger.LogWarning("Token refresh skipped: CloudBaseUrl not configured");
            return null;
        }

        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{RefreshPath}";

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(url, new { refreshToken }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Token refresh HTTP call failed");
            return null;
        }

        // Handle 403 DEVICE_DECOMMISSIONED
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Token refresh returned 403 Forbidden — device may be decommissioned");
            throw new DeviceDecommissionedException("Token refresh returned 403 Forbidden");
        }

        // Handle 401 — refresh token expired or revoked
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Token refresh returned 401 Unauthorized — refresh token expired or revoked, re-provisioning required");
            throw new RefreshTokenExpiredException("Token refresh returned 401 Unauthorized");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Token refresh returned {StatusCode}", response.StatusCode);
            return null;
        }

        TokenRefreshResponse? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync<TokenRefreshResponse>(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to deserialize token refresh response");
            return null;
        }

        if (result?.DeviceToken is null)
        {
            _logger.LogWarning("Token refresh response contained no device token");
            return null;
        }

        // Store both tokens (rotation per spec)
        await _store.SetSecretAsync(TokenKey, result.DeviceToken, ct);
        if (result.RefreshToken is not null)
            await _store.SetSecretAsync(RefreshTokenKey, result.RefreshToken, ct);

        _logger.LogInformation("Device token refreshed successfully");
        return result.DeviceToken;
    }
}
