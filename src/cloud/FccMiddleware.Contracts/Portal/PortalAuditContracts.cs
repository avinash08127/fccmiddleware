using System.Text.Json;

namespace FccMiddleware.Contracts.Portal;

public sealed record AuditEventDto
{
    public required Guid EventId { get; init; }
    public required string EventType { get; init; }
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Source { get; init; }
    public required Guid CorrelationId { get; init; }
    public required Guid LegalEntityId { get; init; }
    public string? SiteCode { get; init; }
    public required JsonElement Payload { get; init; }
}
