using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Events;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Application.Observability;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Infrastructure.Workers;

/// <summary>
/// Background worker that flags stale PENDING transactions.
/// A transaction is stale when status = PENDING, is_stale = false,
/// and created_at &lt; NOW() - stalePendingThresholdDays.
/// This is a FLAG only — status remains PENDING. Odoo can still acknowledge stale transactions.
/// Uses the ix_transactions_stale partial index for efficient scanning.
/// </summary>
public sealed class StaleTransactionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StaleTransactionWorker> _logger;
    private readonly StaleTransactionWorkerOptions _options;

    public StaleTransactionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<StaleTransactionWorker> logger,
        IOptions<StaleTransactionWorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "StaleTransactionWorker started. PollInterval={PollInterval}s, BatchSize={BatchSize}, ThresholdDays={ThresholdDays}",
            _options.PollIntervalSeconds, _options.BatchSize, _options.StalePendingThresholdDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var flagged = await FlagStaleBatchAsync(stoppingToken);

                if (flagged == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
                }
                // If we flagged a full batch, loop immediately to check for more
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StaleTransactionWorker error during batch processing");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorDelaySeconds), stoppingToken);
            }
        }

        _logger.LogInformation("StaleTransactionWorker stopped");
    }

    /// <summary>
    /// Finds PENDING non-stale transactions older than the threshold, flags them as stale,
    /// and publishes a TransactionStaleFlagged event for each. Returns the count flagged.
    /// </summary>
    internal async Task<int> FlagStaleBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var eventPublisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var metrics = scope.ServiceProvider.GetRequiredService<IObservabilityMetrics>();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.StalePendingThresholdDays);

        // Query uses ix_transactions_stale partial index: WHERE status = 'PENDING' AND is_stale = false
        var staleTransactions = await db.Set<Domain.Entities.Transaction>()
            .IgnoreQueryFilters()
            .Where(t => t.Status == TransactionStatus.PENDING
                     && !t.IsStale
                     && t.CreatedAt < cutoff)
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (staleTransactions.Count == 0)
            return 0;

        var now = DateTimeOffset.UtcNow;

        foreach (var tx in staleTransactions)
        {
            tx.IsStale = true;
            tx.UpdatedAt = now;

            eventPublisher.Publish(new TransactionStaleFlagged
            {
                TransactionId = tx.Id,
                FccTransactionId = tx.FccTransactionId,
                StalePendingThresholdDays = _options.StalePendingThresholdDays,
                DetectedAt = now,
                Source = "cloud-stale-detection",
                CorrelationId = tx.CorrelationId,
                LegalEntityId = tx.LegalEntityId,
                SiteCode = tx.SiteCode
            });
        }

        await db.SaveChangesAsync(ct);

        var staleCount = await db.Set<Domain.Entities.Transaction>()
            .IgnoreQueryFilters()
            .CountAsync(t => t.Status == TransactionStatus.PENDING && t.IsStale, ct);
        metrics.RecordStaleTransactionCount(staleCount);

        _logger.LogInformation(
            "StaleTransactionWorker flagged {Count} transactions as stale (threshold={ThresholdDays}d)",
            staleTransactions.Count, _options.StalePendingThresholdDays);

        return staleTransactions.Count;
    }
}

/// <summary>
/// Configuration options for the StaleTransactionWorker.
/// Bound from configuration section "StaleTransactionWorker".
/// </summary>
public sealed class StaleTransactionWorkerOptions
{
    public const string SectionName = "StaleTransactionWorker";

    /// <summary>Seconds between poll cycles when no stale transactions are found. Default: 900 (15 minutes).</summary>
    public int PollIntervalSeconds { get; set; } = 900;

    /// <summary>Max transactions to flag per poll cycle. Default: 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Seconds to wait after an error before retrying. Default: 30.</summary>
    public int ErrorDelaySeconds { get; set; } = 30;

    /// <summary>Days after which a PENDING transaction is considered stale. Default: 3.</summary>
    public int StalePendingThresholdDays { get; set; } = 3;
}
