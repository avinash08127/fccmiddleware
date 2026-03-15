using FccDesktopAgent.Core.Buffer.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// F-DSK-046: Centralized audit log writer that emits <see cref="AuditLogEntry"/> rows
/// for key agent lifecycle events.
/// </summary>
public interface IAuditLogger
{
    Task LogEventAsync(string eventType, string? detail = null,
        string? entityType = null, string? entityId = null,
        string? actor = null, CancellationToken ct = default);
}

public sealed class AuditLogger : IAuditLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(IServiceScopeFactory scopeFactory, ILogger<AuditLogger> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LogEventAsync(string eventType, string? detail = null,
        string? entityType = null, string? entityId = null,
        string? actor = null, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

            db.AuditLog.Add(new AuditLogEntry
            {
                EventType = eventType,
                PayloadJson = detail,
                EntityType = entityType,
                EntityId = entityId,
                Actor = actor ?? "system",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Audit logging should never crash the caller
            _logger.LogWarning(ex, "Failed to write audit log entry: {EventType}", eventType);
        }
    }
}
