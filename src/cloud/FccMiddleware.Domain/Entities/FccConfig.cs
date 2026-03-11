using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Configuration for connecting to and polling from a Forecourt Controller at a site.
/// CredentialRef is a pointer to AWS Secrets Manager — never the credential itself.
/// </summary>
public class FccConfig
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid LegalEntityId { get; set; }
    public FccVendor FccVendor { get; set; }
    public string? FccModel { get; set; }
    public ConnectionProtocol ConnectionProtocol { get; set; }
    public string HostAddress { get; set; } = null!;
    public int Port { get; set; }

    /// <summary>Reference to the credential in AWS Secrets Manager — not the credential itself.</summary>
    public string CredentialRef { get; set; } = null!;

    public IngestionMethod IngestionMethod { get; set; } = IngestionMethod.PUSH;
    public IngestionMode IngestionMode { get; set; } = IngestionMode.CLOUD_DIRECT;
    public int? PullIntervalSeconds { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    public bool IsActive { get; set; } = true;
    public int ConfigVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public Site Site { get; set; } = null!;
    public LegalEntity LegalEntity { get; set; } = null!;
}
