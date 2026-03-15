using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// Periodic background worker that deletes old records past the retention window.
/// Runs every <see cref="AgentConfiguration.CleanupIntervalHours"/> hours (default 24).
/// </summary>
public sealed class CleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly ILogger<CleanupWorker> _logger;

    public CleanupWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<CleanupWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay to let the app finish startup
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup cycle failed");
            }

            var interval = TimeSpan.FromHours(_config.CurrentValue.CleanupIntervalHours);
            await Task.Delay(interval, stoppingToken);
        }
    }

    /// <summary>GAP-2: Days after which Uploaded records are considered stale and reverted to Pending.</summary>
    private const int DefaultStalePendingDays = 3;

    public async Task RunCleanupAsync(CancellationToken ct)
    {
        var retentionDays = _config.CurrentValue.RetentionDays;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var bufferManager = scope.ServiceProvider.GetRequiredService<TransactionBufferManager>();

        // GAP-2: Revert stale Uploaded records back to Pending before retention cleanup.
        // Records stuck at Uploaded for >3 days are likely due to a cloud-side processing delay.
        // Re-uploading is safe because the cloud deduplicates by FccTransactionId.
        var staleReverted = await bufferManager.RevertStaleUploadedAsync(DefaultStalePendingDays, ct);

        // 1. Delete SyncedToOdoo transactions older than retention period
        var txDeleted = await db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.SyncedToOdoo && t.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        // Also delete DuplicateConfirmed past retention
        var dupDeleted = await db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.DuplicateConfirmed && t.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        // GAP-1: Delete DeadLetter records past retention
        var deadLetterDeleted = await db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.DeadLetter && t.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        // F-DSK-041: Delete Archived records past retention (no code transitions to
        // Archived, but records may be set manually via DB — clean them up regardless).
        var archivedDeleted = await db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Archived && t.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        // 2. Delete terminal pre-auth records older than retention period
        var terminalStatuses = new[]
        {
            PreAuthStatus.Completed,
            PreAuthStatus.Cancelled,
            PreAuthStatus.Expired,
            PreAuthStatus.Failed
        };
        var preAuthDeleted = await db.PreAuths
            .Where(p => terminalStatuses.Contains(p.Status) && p.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        // 3. Trim audit log older than retention period
        var auditDeleted = await db.AuditLog
            .Where(a => a.CreatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        _logger.LogInformation(
            "Cleanup completed: {TxDeleted} synced, {DupDeleted} duplicate, {DeadLetterDeleted} dead-lettered, " +
            "{ArchivedDeleted} archived transactions, {PreAuthDeleted} terminal pre-auths, " +
            "{AuditDeleted} audit entries deleted, {StaleReverted} stale uploaded reverted to pending (retention={RetentionDays}d)",
            txDeleted, dupDeleted, deadLetterDeleted, archivedDeleted, preAuthDeleted, auditDeleted, staleReverted, retentionDays);
    }
}
