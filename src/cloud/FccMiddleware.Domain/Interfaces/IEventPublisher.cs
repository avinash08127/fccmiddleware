using FccMiddleware.Domain.Events;

namespace FccMiddleware.Domain.Interfaces;

/// <summary>
/// Publishes domain events by writing them to the outbox table within the current
/// DbContext transaction. The outbox publisher worker processes them asynchronously.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Stages a domain event as an OutboxMessage. The event is persisted atomically
    /// when the caller invokes SaveChangesAsync on the same DbContext.
    /// </summary>
    void Publish(DomainEvent domainEvent);
}
