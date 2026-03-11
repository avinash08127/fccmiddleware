using FccMiddleware.Domain.Common;

namespace FccMiddleware.Contracts.Registration;

public sealed class RefreshTokenResponse
{
    [Sensitive]
    public string DeviceToken { get; set; } = null!;
    [Sensitive]
    public string RefreshToken { get; set; } = null!;
    public DateTimeOffset TokenExpiresAt { get; set; }
}
