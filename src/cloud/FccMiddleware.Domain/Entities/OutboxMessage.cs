namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Transactional outbox message written atomically with the domain entity change.
/// Sequential bigint Id ensures ordered processing by the outbox publisher worker.
/// ProcessedAt is null until the message has been published to the event bus.
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public string EventType { get; set; } = null!;

    /// <summary>Event payload serialized as JSON (jsonb column).</summary>
    public string Payload { get; set; } = null!;

    public Guid CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
