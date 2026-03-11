namespace FccDesktopAgent.Core.Buffer.Entities;

/// <summary>Local audit trail entry for agent events and state transitions.</summary>
public sealed class AuditLogEntry
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Actor { get; set; }
    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
