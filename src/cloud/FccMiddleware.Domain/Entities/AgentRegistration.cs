using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Registration record for an Edge Agent device at a site.
/// The Id is the stable deviceId used in JWT claims.
/// TokenHash stores a one-way hash of the device token — never the token itself.
/// </summary>
public class AgentRegistration : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;
    public string DeviceSerialNumber { get; set; } = null!;
    public string DeviceModel { get; set; } = null!;
    public string OsVersion { get; set; } = null!;
    public string AgentVersion { get; set; } = null!;
    public bool IsActive { get; set; } = true;

    /// <summary>Hashed device JWT — never store the plaintext token.</summary>
    public string TokenHash { get; set; } = null!;

    public DateTimeOffset TokenExpiresAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public Site Site { get; set; } = null!;
    public LegalEntity LegalEntity { get; set; } = null!;
}
