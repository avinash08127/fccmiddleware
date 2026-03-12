namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Singleton operational settings record used by portal admin APIs.
/// JSON blobs keep the persisted schema flexible while the API exposes typed contracts.
/// </summary>
public class PortalSettings
{
    public Guid Id { get; set; }
    public string GlobalDefaultsJson { get; set; } = null!;
    public string AlertConfigurationJson { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
