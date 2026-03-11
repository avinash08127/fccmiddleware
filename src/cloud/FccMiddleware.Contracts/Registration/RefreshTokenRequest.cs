using FccMiddleware.Domain.Common;

namespace FccMiddleware.Contracts.Registration;

public sealed class RefreshTokenRequest
{
    [Sensitive]
    public string RefreshToken { get; set; } = null!;
}
