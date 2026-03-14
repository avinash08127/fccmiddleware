using FccMiddleware.Domain.Enums;
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
    public string DeviceClass { get; set; } = "ANDROID";
    public string RoleCapability { get; set; } = "PRIMARY_ELIGIBLE";
    public int SiteHaPriority { get; set; } = 100;
    public string? CurrentRole { get; set; }
    public string CapabilitiesJson { get; set; } = "[]";
    public string? PeerApiBaseUrl { get; set; }
    public string? PeerApiAdvertisedHost { get; set; }
    public int? PeerApiPort { get; set; }
    public bool PeerApiTlsEnabled { get; set; }
    public long? LeaderEpochSeen { get; set; }
    public int? LastReplicationLagSeconds { get; set; }
    public AgentRegistrationStatus Status { get; set; } = AgentRegistrationStatus.ACTIVE;
    public bool IsActive { get; set; } = true;

    /// <summary>Hashed device JWT — never store the plaintext token.</summary>
    public string? TokenHash { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? SuspensionReasonCode { get; set; }
    public string? SuspensionReason { get; set; }
    public Guid? ReplacementForDeviceId { get; set; }
    public DateTimeOffset? ApprovalGrantedAt { get; set; }
    public string? ApprovalGrantedByActorId { get; set; }
    public string? ApprovalGrantedByActorDisplay { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation properties
    public Site Site { get; set; } = null!;
    public LegalEntity LegalEntity { get; set; } = null!;
}
