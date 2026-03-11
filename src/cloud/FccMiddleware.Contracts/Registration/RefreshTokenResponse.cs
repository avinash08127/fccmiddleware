namespace FccMiddleware.Contracts.Registration;

public sealed class RefreshTokenResponse
{
    public string DeviceToken { get; set; } = null!;
    public string RefreshToken { get; set; } = null!;
    public DateTimeOffset TokenExpiresAt { get; set; }
}
