using FccMiddleware.Application.Observability;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Infrastructure.Workers;

public sealed class MonitoringSnapshotWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MonitoringSnapshotWorker> _logger;
    private readonly MonitoringSnapshotWorkerOptions _options;

    public MonitoringSnapshotWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<MonitoringSnapshotWorker> logger,
        IOptions<MonitoringSnapshotWorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishSnapshotsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitoring snapshot worker failed");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorDelaySeconds), stoppingToken);
            }
        }
    }

    private async Task PublishSnapshotsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var metrics = scope.ServiceProvider.GetRequiredService<IObservabilityMetrics>();
        var now = DateTimeOffset.UtcNow;

        var agents = await db.AgentRegistrations
            .IgnoreQueryFilters()
            .Where(a => a.IsActive)
            .ToListAsync(ct);

        foreach (var agent in agents)
        {
            var reference = agent.LastSeenAt ?? agent.RegisteredAt;
            var offlineHours = Math.Max(0, (now - reference).TotalHours);
            metrics.RecordEdgeAgentOfflineHours(agent.LegalEntityId, agent.SiteCode, agent.Id, offlineHours);
        }

        var staleCount = await db.Transactions
            .IgnoreQueryFilters()
            .CountAsync(
                t => t.Status == Domain.Enums.TransactionStatus.PENDING && t.IsStale,
                ct);

        metrics.RecordStaleTransactionCount(staleCount);
    }
}

public sealed class MonitoringSnapshotWorkerOptions
{
    public const string SectionName = "MonitoringSnapshotWorker";

    public int PollIntervalSeconds { get; set; } = 300;

    public int ErrorDelaySeconds { get; set; } = 30;
}
