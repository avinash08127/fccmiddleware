using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Events;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.Persistence;

namespace FccMiddleware.Infrastructure.Events;

/// <summary>
/// Writes domain events to the outbox_messages table via the DbContext.
/// The event is staged (not committed) — the caller must call SaveChangesAsync
/// to persist both the domain entity change and the outbox message atomically.
/// </summary>
public sealed class OutboxEventPublisher : IEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly FccMiddlewareDbContext _db;

    public OutboxEventPublisher(FccMiddlewareDbContext db)
    {
        _db = db;
    }

    public void Publish(DomainEvent domainEvent)
    {
        var envelope = new
        {
            domainEvent.EventId,
            domainEvent.EventType,
            domainEvent.SchemaVersion,
            Timestamp = domainEvent.Timestamp.ToString("o"),
            domainEvent.Source,
            domainEvent.CorrelationId,
            domainEvent.LegalEntityId,
            domainEvent.SiteCode,
            Payload = domainEvent
        };

        var message = new OutboxMessage
        {
            EventType = domainEvent.EventType,
            Payload = JsonSerializer.Serialize(envelope, JsonOptions),
            CorrelationId = domainEvent.CorrelationId,
            CreatedAt = domainEvent.Timestamp
        };

        _db.OutboxMessages.Add(message);
    }
}
