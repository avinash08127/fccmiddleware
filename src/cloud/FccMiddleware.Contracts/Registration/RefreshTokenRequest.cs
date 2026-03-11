namespace FccMiddleware.Contracts.Registration;

public sealed class RefreshTokenRequest
{
    public string RefreshToken { get; set; } = null!;
}
