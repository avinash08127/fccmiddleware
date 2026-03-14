using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Authoritative cloud-side command row for a registered edge agent.
/// Polling remains the source of truth even when Android push hints are enabled.
/// </summary>
public class AgentCommand : ITenantScoped
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;
    public AgentCommandType CommandType { get; set; }
    public string Reason { get; set; } = null!;
    public string? PayloadJson { get; set; }
    public AgentCommandStatus Status { get; set; } = AgentCommandStatus.PENDING;
    public string? CreatedByActorId { get; set; }
    public string? CreatedByActorDisplay { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? HandledAtUtc { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public string? ResultJson { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }

    public AgentRegistration Device { get; set; } = null!;
}
