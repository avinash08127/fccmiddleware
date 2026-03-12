namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Thrown when the token refresh endpoint returns HTTP 401, indicating that the
/// refresh token has expired or been revoked. The device must be re-provisioned
/// with a new bootstrap token.
/// </summary>
public sealed class RefreshTokenExpiredException : Exception
{
    public RefreshTokenExpiredException(string reason)
        : base($"Refresh token expired — re-provisioning required: {reason}") { }
}
