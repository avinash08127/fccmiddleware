using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Security;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Registration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Retrieves and refreshes the device JWT used for cloud API authentication.
/// The active JWT + refresh token pair is stored atomically under
/// <see cref="TokenBundleKey"/>, with staging/pending markers used to recover
/// safely from interrupted refresh writes. Legacy per-token keys are still read
/// for backward-compatible migration.
///
/// Refresh sends the tokens in the JSON body (NOT as Bearer header) per spec.
/// Both tokens are rotated on every successful refresh (token rotation).
/// </summary>
public sealed class DeviceTokenProvider : IDeviceTokenProvider
{
    internal const string TokenKey = CredentialKeys.DeviceToken;
    internal const string RefreshTokenKey = CredentialKeys.RefreshToken;
    internal const string TokenBundleKey = CredentialKeys.DeviceTokenBundle;
    internal const string TokenBundleStagingKey = CredentialKeys.DeviceTokenBundleStaging;
    internal const string RefreshPendingKey = CredentialKeys.DeviceTokenRefreshPending;
    private const string RefreshPath = "/api/v1/agent/token/refresh";
    private static readonly JsonSerializerOptions TokenBundleJson = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ICredentialStore _store;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly ILogger<DeviceTokenProvider> _logger;

    // P-DSK-007: Skip unnecessary staging recovery checks after the first successful check.
    // Staging keys only exist during the brief window of an interrupted refresh (rare crash).
    private volatile bool _stagingRecoveryAttempted;

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

    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        // P-DSK-007: Only attempt staging recovery once — staging keys only exist
        // during interrupted refresh (rare). Skip on subsequent calls to avoid a
        // wasted credential store read per token access.
        if (!_stagingRecoveryAttempted)
        {
            var recovered = await TryRecoverStagedTokenBundleAsync(ct);
            _stagingRecoveryAttempted = true;
            if (recovered is not null)
                return recovered.DeviceToken;
        }

        var bundle = await TryLoadActiveTokenBundleAsync(ct);
        if (bundle is not null)
            return bundle.DeviceToken;

        return await _store.GetSecretAsync(TokenKey, ct);
    }

    public async Task StoreTokensAsync(string deviceToken, string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        await CommitTokenBundleAsync(new StoredTokenBundle(deviceToken, refreshToken), ct);
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
        var recovered = await TryRecoverStagedTokenBundleAsync(ct);
        if (recovered is not null)
        {
            _logger.LogWarning("Recovered staged device token bundle after interrupted refresh write");
            return recovered.DeviceToken;
        }

        if (await HasUnrecoverablePendingRefreshAsync(ct))
        {
            _logger.LogCritical(
                "Detected an interrupted token refresh without recoverable staged tokens. " +
                "The previous refresh may have completed on the server, so the stored refresh token is no longer safe to reuse.");
            throw new RefreshTokenExpiredException(
                "Previous token refresh did not complete locally; re-provisioning is required.");
        }

        var bundle = await TryLoadActiveTokenBundleAsync(ct);
        var refreshToken = bundle?.RefreshToken ?? await _store.GetSecretAsync(RefreshTokenKey, ct);
        var deviceToken = bundle?.DeviceToken ?? await _store.GetSecretAsync(TokenKey, ct);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogWarning("Token refresh skipped: no refresh token in credential store");
            return null;
        }

        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            _logger.LogWarning("Token refresh cannot proceed: no device token available to bind the refresh request");
            throw new RefreshTokenExpiredException("Device token is missing; re-provisioning is required.");
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
            await MarkRefreshPendingAsync(ct);
            response = await http.PostAsJsonAsync(url, new RefreshTokenRequest
            {
                RefreshToken = refreshToken,
                DeviceToken = deviceToken
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Token refresh HTTP call failed");
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            await DeleteSecretBestEffortAsync(RefreshPendingKey, ct);
            var error = await TryReadErrorAsync(response, ct);
            if (string.Equals(error?.ErrorCode, "DEVICE_DECOMMISSIONED", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Token refresh returned 403 DEVICE_DECOMMISSIONED");
                throw new DeviceDecommissionedException(error?.Message ?? "Token refresh returned 403 DEVICE_DECOMMISSIONED");
            }

            _logger.LogWarning("Token refresh returned 403 {ErrorCode}", error?.ErrorCode ?? "FORBIDDEN");
            throw new RefreshTokenExpiredException(error?.Message ?? "Token refresh returned 403 Forbidden");
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await DeleteSecretBestEffortAsync(RefreshPendingKey, ct);
            var error = await TryReadErrorAsync(response, ct);
            _logger.LogWarning("Token refresh returned 401 {ErrorCode}", error?.ErrorCode ?? "UNAUTHORIZED");
            throw new RefreshTokenExpiredException(error?.Message ?? "Token refresh returned 401 Unauthorized");
        }

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode < 500)
                await DeleteSecretBestEffortAsync(RefreshPendingKey, ct);
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

        if (string.IsNullOrWhiteSpace(result.RefreshToken))
        {
            _logger.LogWarning("Token refresh response contained no refresh token");
            return null;
        }

        await CommitTokenBundleAsync(new StoredTokenBundle(result.DeviceToken, result.RefreshToken), ct);

        _logger.LogInformation("Device token refreshed successfully");
        return result.DeviceToken;
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

    private async Task<StoredTokenBundle?> TryLoadActiveTokenBundleAsync(CancellationToken ct)
    {
        var bundle = await TryReadTokenBundleAsync(TokenBundleKey, ct);
        if (bundle is not null)
            return bundle;

        var legacyDeviceToken = await _store.GetSecretAsync(TokenKey, ct);
        var legacyRefreshToken = await _store.GetSecretAsync(RefreshTokenKey, ct);

        if (string.IsNullOrWhiteSpace(legacyDeviceToken) || string.IsNullOrWhiteSpace(legacyRefreshToken))
            return null;

        var legacyBundle = new StoredTokenBundle(legacyDeviceToken, legacyRefreshToken);
        await TryMigrateLegacyTokenBundleAsync(legacyBundle, ct);
        return legacyBundle;
    }

    private async Task<StoredTokenBundle?> TryRecoverStagedTokenBundleAsync(CancellationToken ct)
    {
        var staged = await TryReadTokenBundleAsync(TokenBundleStagingKey, ct);
        if (staged is null)
            return null;

        // T-DSK-011: If active already matches staged, the process crashed after writing
        // active but before cleaning up the staging key. Just clean up — no promotion needed.
        var active = await TryReadTokenBundleAsync(TokenBundleKey, ct);
        if (active is not null
            && active.DeviceToken == staged.DeviceToken
            && active.RefreshToken == staged.RefreshToken)
        {
            _logger.LogDebug("Staged token bundle matches active — cleaning up stale staging key");
            await ClearRefreshMarkersBestEffortAsync(ct);
            return active;
        }

        try
        {
            await _store.SetSecretAsync(TokenBundleKey, SerializeTokenBundle(staged), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to promote staged device token bundle into the active credential entry");
            return staged;
        }

        await DeleteLegacyKeysBestEffortAsync(ct);
        await ClearRefreshMarkersBestEffortAsync(ct);
        return staged;
    }

    private async Task<bool> HasUnrecoverablePendingRefreshAsync(CancellationToken ct)
    {
        var pending = await _store.GetSecretAsync(RefreshPendingKey, ct);
        if (string.IsNullOrWhiteSpace(pending))
            return false;

        var staged = await TryReadTokenBundleAsync(TokenBundleStagingKey, ct);
        return staged is null;
    }

    private Task MarkRefreshPendingAsync(CancellationToken ct)
    {
        // P-DSK-007: Reset the staging recovery flag when a new refresh starts,
        // so the next GetTokenAsync will check for staged bundles again.
        _stagingRecoveryAttempted = false;
        return _store.SetSecretAsync(RefreshPendingKey, DateTimeOffset.UtcNow.ToString("O"), ct);
    }

    private async Task CommitTokenBundleAsync(StoredTokenBundle bundle, CancellationToken ct)
    {
        var serialized = SerializeTokenBundle(bundle);

        await _store.SetSecretAsync(TokenBundleStagingKey, serialized, ct);
        await _store.SetSecretAsync(TokenBundleKey, serialized, ct);

        // T-DSK-011: Delete staging key immediately after active write succeeds.
        // This prevents unnecessary recovery on next startup if the process
        // crashes between writing active and the full cleanup phase.
        await DeleteSecretBestEffortAsync(TokenBundleStagingKey, ct);

        await DeleteLegacyKeysBestEffortAsync(ct);
        await DeleteSecretBestEffortAsync(RefreshPendingKey, ct);
    }

    private async Task TryMigrateLegacyTokenBundleAsync(StoredTokenBundle bundle, CancellationToken ct)
    {
        try
        {
            await _store.SetSecretAsync(TokenBundleKey, SerializeTokenBundle(bundle), ct);
            await DeleteLegacyKeysBestEffortAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to migrate legacy token credentials into the atomic token bundle entry");
        }
    }

    private async Task<StoredTokenBundle?> TryReadTokenBundleAsync(string key, CancellationToken ct)
    {
        var json = await _store.GetSecretAsync(key, ct);
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var bundle = JsonSerializer.Deserialize<StoredTokenBundle>(json, TokenBundleJson);
            if (string.IsNullOrWhiteSpace(bundle?.DeviceToken) || string.IsNullOrWhiteSpace(bundle.RefreshToken))
            {
                _logger.LogWarning("Credential entry {Key} contains an incomplete token bundle", key);
                return null;
            }

            return bundle;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Credential entry {Key} contains invalid token bundle JSON", key);
            return null;
        }
    }

    // T-DSK-011: Unconditionally clean both markers. The old short-circuit
    // (skip staging delete if pending delete fails) could leave a stale staging
    // key that triggers unnecessary recovery on next startup.
    private async Task ClearRefreshMarkersBestEffortAsync(CancellationToken ct)
    {
        await DeleteSecretBestEffortAsync(RefreshPendingKey, ct);
        await DeleteSecretBestEffortAsync(TokenBundleStagingKey, ct);
    }

    private async Task DeleteLegacyKeysBestEffortAsync(CancellationToken ct)
    {
        await DeleteSecretBestEffortAsync(TokenKey, ct);
        await DeleteSecretBestEffortAsync(RefreshTokenKey, ct);
    }

    private async Task DeleteSecretBestEffortAsync(string key, CancellationToken ct)
    {
        await TryDeleteSecretBestEffortAsync(key, ct);
    }

    private async Task<bool> TryDeleteSecretBestEffortAsync(string key, CancellationToken ct)
    {
        try
        {
            await _store.DeleteSecretAsync(key, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to delete credential entry {Key} during token bundle cleanup", key);
            return false;
        }
    }

    private static string SerializeTokenBundle(StoredTokenBundle bundle) =>
        JsonSerializer.Serialize(bundle, TokenBundleJson);

    private sealed record StoredTokenBundle(string DeviceToken, string RefreshToken);
}
