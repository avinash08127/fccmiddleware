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

    public async Task RunCleanupAsync(CancellationToken ct)
    {
        var retentionDays = _config.CurrentValue.RetentionDays;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

        // 1. Delete SyncedToOdoo transactions older than retention period
        var txDeleted = await db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.SyncedToOdoo && t.UpdatedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        // Also delete DuplicateConfirmed past retention
        var dupDeleted = await db.Transactions
            .Where(t => t.SyncStatus == SyncStatus.DuplicateConfirmed && t.UpdatedAt < cutoff)
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
            "Cleanup completed: {TxDeleted} synced transactions, {DupDeleted} duplicate transactions, " +
            "{PreAuthDeleted} terminal pre-auths, {AuditDeleted} audit entries deleted (retention={RetentionDays}d)",
            txDeleted, dupDeleted, preAuthDeleted, auditDeleted, retentionDays);
    }
}
