namespace FccMiddleware.Contracts.Registration;

public sealed class RevokeBootstrapTokenResponse
{
    public Guid TokenId { get; set; }
    public DateTimeOffset RevokedAt { get; set; }
}
