using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Infrastructure.Workers;

/// <summary>
/// Re-runs reconciliation for deferred unmatched records and escalates any records
/// that age beyond the 24-hour automatic retry window.
/// </summary>
public sealed class UnmatchedReconciliationWorker : BackgroundService
{
    private static readonly TimeSpan GiveUpAge = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UnmatchedReconciliationWorker> _logger;
    private readonly UnmatchedReconciliationWorkerOptions _options;

    public UnmatchedReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<UnmatchedReconciliationWorker> logger,
        IOptions<UnmatchedReconciliationWorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "UnmatchedReconciliationWorker started. PollInterval={PollInterval}s, BatchSize={BatchSize}",
            _options.PollIntervalSeconds,
            _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UnmatchedReconciliationWorker error during batch processing");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorDelaySeconds), stoppingToken);
            }
        }

        _logger.LogInformation("UnmatchedReconciliationWorker stopped");
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReconciliationDbContext>();
        var matcher = scope.ServiceProvider.GetRequiredService<ReconciliationMatchingService>();

        var now = DateTimeOffset.UtcNow;
        var dueItems = await db.FindDueUnmatchedRetriesAsync(now, _options.BatchSize, ct);
        if (dueItems.Count == 0)
        {
            return 0;
        }

        var retried = 0;
        var escalated = 0;

        foreach (var item in dueItems)
        {
            if (now - item.Reconciliation.CreatedAt > GiveUpAge)
            {
                var escalation = matcher.EscalateUnmatched(item.Transaction, item.Reconciliation, now);
                if (escalation.CreatedOrUpdated)
                {
                    escalated++;
                }

                continue;
            }

            var retryResult = await matcher.RetryUnmatchedAsync(item.Transaction, item.Reconciliation, ct);
            if (retryResult.CreatedOrUpdated)
            {
                retried++;
            }
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "UnmatchedReconciliationWorker processed {Count} records ({Retried} retried, {Escalated} escalated)",
            dueItems.Count,
            retried,
            escalated);

        return dueItems.Count;
    }
}

public sealed class UnmatchedReconciliationWorkerOptions
{
    public const string SectionName = "UnmatchedReconciliationWorker";

    public int PollIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 500;
    public int ErrorDelaySeconds { get; set; } = 30;
}
