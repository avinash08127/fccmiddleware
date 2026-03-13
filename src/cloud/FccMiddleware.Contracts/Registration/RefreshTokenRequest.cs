using FccMiddleware.Domain.Common;

namespace FccMiddleware.Contracts.Registration;

public sealed class RefreshTokenRequest
{
    [Sensitive]
    public string RefreshToken { get; set; } = null!;

    /// <summary>
    /// FM-S03: The current (even expired) device JWT. Required to bind the
    /// refresh operation to the original device identity.
    /// </summary>
    [Sensitive]
    public string DeviceToken { get; set; } = null!;
}
