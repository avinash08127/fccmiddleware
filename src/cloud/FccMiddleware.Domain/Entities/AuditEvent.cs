using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Append-only audit event. Partitioned by CreatedAt (monthly range).
/// The composite PK is (Id, CreatedAt) as required for PostgreSQL range partitioning.
/// Payload is stored as serialized JSON (jsonb column).
/// No UpdatedAt — events are immutable.
/// </summary>
public class AuditEvent : ITenantScoped
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }  // Partition key — part of composite PK
    public Guid LegalEntityId { get; set; }
    public string EventType { get; set; } = null!;
    public Guid CorrelationId { get; set; }
    public string? SiteCode { get; set; }
    public string Source { get; set; } = null!;

    /// <summary>Event-specific payload stored as JSON in a jsonb column.</summary>
    public string Payload { get; set; } = null!;

    /// <summary>
    /// The primary entity this event pertains to (e.g. DeviceId for agent-scoped events).
    /// Null for events not tied to a specific entity. Indexed for direct lookup by device/entity.
    /// </summary>
    public Guid? EntityId { get; set; }
}
