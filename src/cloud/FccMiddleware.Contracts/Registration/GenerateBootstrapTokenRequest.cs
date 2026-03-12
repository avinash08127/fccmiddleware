namespace FccMiddleware.Contracts.Registration;

public sealed class GenerateBootstrapTokenRequest
{
    public string SiteCode { get; set; } = null!;
    public Guid LegalEntityId { get; set; }

    /// <summary>Cloud environment key (e.g. "PRODUCTION", "STAGING"). Optional — null for legacy registrations.</summary>
    public string? Environment { get; set; }
}
