using FccDesktopAgent.Core.Registration;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Outcome of an authenticated cloud request executed by <see cref="AuthenticatedCloudRequestHandler"/>.
/// </summary>
public enum AuthRequestOutcome
{
    /// <summary>Request completed successfully.</summary>
    Success,

    /// <summary>No device token available (device not yet registered).</summary>
    NoToken,

    /// <summary>Refresh token expired — device requires re-provisioning.</summary>
    ReprovisioningRequired,

    /// <summary>Device has been decommissioned by the cloud backend.</summary>
    Decommissioned,

    /// <summary>Token refresh returned null.</summary>
    AuthFailed,

    /// <summary>Transport or other non-auth error.</summary>
    Failed,
}

/// <summary>
/// Encapsulates the result of an authenticated cloud request.
/// </summary>
public sealed class AuthRequestResult<T>
{
    public AuthRequestOutcome Outcome { get; private init; }
    public T? Value { get; private init; }
    public Exception? Error { get; private init; }

    public bool IsSuccess => Outcome == AuthRequestOutcome.Success;

    /// <summary>
    /// Whether the device should halt cloud communication (decommissioned or needs re-provisioning).
    /// </summary>
    public bool RequiresHalt => Outcome is AuthRequestOutcome.Decommissioned
                                          or AuthRequestOutcome.ReprovisioningRequired;

    internal static AuthRequestResult<T> Ok(T value) => new() { Outcome = AuthRequestOutcome.Success, Value = value };
    internal static AuthRequestResult<T> NoToken() => new() { Outcome = AuthRequestOutcome.NoToken };
    internal static AuthRequestResult<T> ReprovisioningRequired() => new() { Outcome = AuthRequestOutcome.ReprovisioningRequired };
    internal static AuthRequestResult<T> Decommissioned() => new() { Outcome = AuthRequestOutcome.Decommissioned };
    internal static AuthRequestResult<T> AuthFailed() => new() { Outcome = AuthRequestOutcome.AuthFailed };
    internal static AuthRequestResult<T> TransportError(Exception ex) => new() { Outcome = AuthRequestOutcome.Failed, Error = ex };
}

/// <summary>
/// Centralizes the "get token -> send request -> on 401, refresh and retry -> on decommission, halt"
/// pattern used by all cloud sync workers (T-DSK-010).
///
/// Handles <see cref="RefreshTokenExpiredException"/> and <see cref="DeviceDecommissionedException"/>
/// uniformly, calling <see cref="IRegistrationManager.MarkReprovisioningRequiredAsync"/> or
/// <see cref="IRegistrationManager.MarkDecommissionedAsync"/> as appropriate.
/// </summary>
public sealed class AuthenticatedCloudRequestHandler
{
    private readonly IDeviceTokenProvider _tokenProvider;
    private readonly IRegistrationManager _registrationManager;
    private readonly ILogger<AuthenticatedCloudRequestHandler> _logger;

    public AuthenticatedCloudRequestHandler(
        IDeviceTokenProvider tokenProvider,
        IRegistrationManager registrationManager,
        ILogger<AuthenticatedCloudRequestHandler> logger)
    {
        _tokenProvider = tokenProvider;
        _registrationManager = registrationManager;
        _logger = logger;
    }

    /// <summary>
    /// Acquires a device token and executes <paramref name="requestFactory"/>. On HTTP 401
    /// (signalled by <see cref="UnauthorizedAccessException"/>), refreshes the token and retries once.
    /// <see cref="OperationCanceledException"/> propagates directly to the caller.
    /// </summary>
    /// <typeparam name="T">The response type returned by the cloud request.</typeparam>
    /// <param name="requestFactory">
    /// A delegate that sends the HTTP request using the provided Bearer token.
    /// Must throw <see cref="UnauthorizedAccessException"/> on HTTP 401
    /// and <see cref="DeviceDecommissionedException"/> on HTTP 403 with DEVICE_DECOMMISSIONED.
    /// </param>
    /// <param name="callerName">Caller name for structured logging.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AuthRequestResult<T>> ExecuteAsync<T>(
        Func<string, CancellationToken, Task<T>> requestFactory,
        string callerName,
        CancellationToken ct)
    {
        var token = await _tokenProvider.GetTokenAsync(ct);
        if (token is null)
        {
            _logger.LogWarning("{Caller} skipped: no device token available", callerName);
            return AuthRequestResult<T>.NoToken();
        }

        try
        {
            var value = await requestFactory(token, ct);
            return AuthRequestResult<T>.Ok(value);
        }
        catch (UnauthorizedAccessException)
        {
            // 401: refresh token once and retry
            _logger.LogWarning("{Caller} received 401; refreshing device token", callerName);

            string? newToken;
            try
            {
                newToken = await _tokenProvider.RefreshTokenAsync(ct);
            }
            catch (RefreshTokenExpiredException rex)
            {
                await _registrationManager.MarkReprovisioningRequiredAsync();
                _logger.LogCritical(
                    "REFRESH_TOKEN_EXPIRED during {Caller}: device requires re-provisioning. " +
                    "Reason: {Reason}", callerName, rex.Message);
                return AuthRequestResult<T>.ReprovisioningRequired();
            }
            catch (DeviceDecommissionedException dex)
            {
                await _registrationManager.MarkDecommissionedAsync();
                _logger.LogCritical(
                    "DEVICE_DECOMMISSIONED during {Caller} token refresh. " +
                    "Reason: {Reason}", callerName, dex.Message);
                return AuthRequestResult<T>.Decommissioned();
            }

            if (newToken is null)
            {
                _logger.LogWarning("Token refresh returned null; {Caller} aborted", callerName);
                return AuthRequestResult<T>.AuthFailed();
            }

            try
            {
                var value = await requestFactory(newToken, ct);
                return AuthRequestResult<T>.Ok(value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Caller} failed after token refresh", callerName);
                return AuthRequestResult<T>.TransportError(ex);
            }
        }
        catch (DeviceDecommissionedException ex)
        {
            await _registrationManager.MarkDecommissionedAsync();
            _logger.LogCritical(
                "DEVICE_DECOMMISSIONED during {Caller}. All cloud sync halted. " +
                "Agent restart required. Reason: {Reason}", callerName, ex.Message);
            return AuthRequestResult<T>.Decommissioned();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Caller} request failed", callerName);
            return AuthRequestResult<T>.TransportError(ex);
        }
    }
}
