namespace FccMiddleware.Contracts.Registration;

public sealed class GenerateBootstrapTokenRequest
{
    public string SiteCode { get; set; } = null!;
    public Guid LegalEntityId { get; set; }
}
