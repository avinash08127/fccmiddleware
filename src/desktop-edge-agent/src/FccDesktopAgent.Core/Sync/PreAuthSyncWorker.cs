using FccDesktopAgent.Core.Buffer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// F-DSK-044: Placeholder pre-auth sync worker.
/// Currently logs unsent pre-auth records count for diagnostics.
/// When the cloud API adds a pre-auth upload endpoint, this worker will
/// drain pre_auth_records WHERE IsCloudSynced = false and upload them.
/// </summary>
public sealed class PreAuthSyncWorker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PreAuthSyncWorker> _logger;

    public PreAuthSyncWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PreAuthSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Logs the count of unsent pre-auth records for operational visibility.
    /// TODO: Upload to cloud when the pre-auth sync API endpoint is available.
    /// </summary>
    public async Task<int> SyncPendingAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        var unsentCount = await db.PreAuths
            .Where(p => !p.IsCloudSynced)
            .CountAsync(ct);

        if (unsentCount > 0)
        {
            _logger.LogDebug(
                "Pre-auth cloud sync: {Count} record(s) pending upload (cloud API endpoint not yet available)",
                unsentCount);
        }

        return unsentCount;
    }
}
