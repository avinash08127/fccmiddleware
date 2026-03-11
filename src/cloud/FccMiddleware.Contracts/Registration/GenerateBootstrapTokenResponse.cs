namespace FccMiddleware.Contracts.Registration;

public sealed class GenerateBootstrapTokenResponse
{
    public Guid TokenId { get; set; }
    public string RawToken { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public string SiteCode { get; set; } = null!;
}
