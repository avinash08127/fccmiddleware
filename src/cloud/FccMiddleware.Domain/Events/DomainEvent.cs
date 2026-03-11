namespace FccMiddleware.Domain.Events;

/// <summary>
/// Base class for all domain events in the Forecourt Middleware Platform.
/// Follows the event envelope schema defined in event-schema-design.md §5.1.
/// </summary>
public abstract class DomainEvent
{
    /// <summary>Unique identifier for this event instance (UUID v4).</summary>
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>PascalCase event type name matching the event-envelope schema enum.</summary>
    public abstract string EventType { get; }

    /// <summary>Payload schema version for this event type. Starts at 1.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>UTC timestamp of when the domain event occurred.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Producing system identifier (e.g., cloud-ingestion, cloud-reconciliation).</summary>
    public string Source { get; init; } = null!;

    /// <summary>Links related events across the processing chain.</summary>
    public Guid CorrelationId { get; init; }

    /// <summary>Tenant scope.</summary>
    public Guid LegalEntityId { get; init; }

    /// <summary>Site identifier. Null for legal-entity-scoped events.</summary>
    public string? SiteCode { get; init; }
}
