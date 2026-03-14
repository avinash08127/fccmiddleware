using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Device-owned push installation used for Android FCM wake-up hints.
/// Registration tokens are encrypted at rest.
/// </summary>
public class AgentInstallation : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;
    public string Platform { get; set; } = "ANDROID";
    public string PushProvider { get; set; } = "FCM";
    public string RegistrationToken { get; set; } = null!;
    public string TokenHash { get; set; } = null!;
    public string AppVersion { get; set; } = null!;
    public string OsVersion { get; set; } = null!;
    public string DeviceModel { get; set; } = null!;
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? LastHintSentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AgentRegistration Device { get; set; } = null!;
}
