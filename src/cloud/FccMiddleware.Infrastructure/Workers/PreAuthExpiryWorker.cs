using FccMiddleware.Application.Ingestion;
using FccMiddleware.Application.PreAuth;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Infrastructure.Workers;

/// <summary>
/// Expires active pre-auth records whose expires_at has passed.
/// Active states are PENDING, AUTHORIZED, and DISPENSING.
/// </summary>
public sealed class PreAuthExpiryWorker : BackgroundService
{
    private static readonly PreAuthStatus[] ExpirableStatuses =
    [
        PreAuthStatus.PENDING,
        PreAuthStatus.AUTHORIZED,
        PreAuthStatus.DISPENSING
    ];

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PreAuthExpiryWorker> _logger;
    private readonly PreAuthExpiryWorkerOptions _options;

    public PreAuthExpiryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PreAuthExpiryWorker> logger,
        IOptions<PreAuthExpiryWorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "PreAuthExpiryWorker started. PollInterval={PollInterval}s, BatchSize={BatchSize}",
            _options.PollIntervalSeconds,
            _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var expired = await ExpireBatchAsync(stoppingToken);
                if (expired == 0)
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PreAuthExpiryWorker error during batch processing");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorDelaySeconds), stoppingToken);
            }
        }

        _logger.LogInformation("PreAuthExpiryWorker stopped");
    }

    internal async Task<int> ExpireBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var eventPublisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var now = DateTimeOffset.UtcNow;
        var expiredRecords = await db.PreAuthRecords
            .IgnoreQueryFilters()
            .Where(p =>
                (p.Status == PreAuthStatus.PENDING
              || p.Status == PreAuthStatus.AUTHORIZED
              || p.Status == PreAuthStatus.DISPENSING)
             && p.ExpiresAt <= now)
            .OrderBy(p => p.ExpiresAt)
            .ThenBy(p => p.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (expiredRecords.Count == 0)
            return 0;

        var dispensingExpired = new List<(Domain.Entities.PreAuthRecord Record, Guid CorrelationId)>();

        foreach (var record in expiredRecords)
        {
            var previousStatus = record.Status;
            var correlationId = Guid.NewGuid();

            record.Transition(PreAuthStatus.EXPIRED);
            record.ExpiredAt ??= now;

            eventPublisher.Publish(PreAuthEventFactory.CreateForStatus(
                record,
                correlationId,
                source: "cloud-preauth"));

            if (previousStatus == PreAuthStatus.DISPENSING)
                dispensingExpired.Add((record, correlationId));
        }

        await db.SaveChangesAsync(ct);

        foreach (var item in dispensingExpired)
            await TryDeauthorizePumpAsync(scope.ServiceProvider, item.Record, item.CorrelationId, ct);

        _logger.LogInformation(
            "PreAuthExpiryWorker expired {Count} pre-auth records",
            expiredRecords.Count);

        return expiredRecords.Count;
    }

    private async Task TryDeauthorizePumpAsync(
        IServiceProvider serviceProvider,
        Domain.Entities.PreAuthRecord record,
        Guid correlationId,
        CancellationToken ct)
    {
        var siteConfigProvider = serviceProvider.GetRequiredService<ISiteFccConfigProvider>();
        var adapterFactory = serviceProvider.GetRequiredService<IFccAdapterFactory>();

        try
        {
            var siteConfig = await siteConfigProvider.GetBySiteCodeAsync(record.SiteCode, ct);
            if (siteConfig is null)
            {
                _logger.LogWarning(
                    "Pre-auth {PreAuthId} expired from DISPENSING but FCC deauthorization was skipped because no active site config was found. CorrelationId={CorrelationId}",
                    record.Id,
                    correlationId);
                return;
            }

            var adapter = adapterFactory.Resolve(siteConfig.Value.Config.FccVendor, siteConfig.Value.Config);
            if (adapter is not IFccPumpDeauthorizationAdapter deauthAdapter)
            {
                _logger.LogInformation(
                    "Pre-auth {PreAuthId} expired from DISPENSING but FCC deauthorization is unavailable for vendor {Vendor}. CorrelationId={CorrelationId}",
                    record.Id,
                    siteConfig.Value.Config.FccVendor,
                    correlationId);
                return;
            }

            await deauthAdapter.DeauthorizePumpAsync(record.PumpNumber, record.NozzleNumber, ct);

            _logger.LogInformation(
                "Pre-auth {PreAuthId} expired from DISPENSING and FCC pump deauthorization was attempted for pump {PumpNumber} nozzle {NozzleNumber}. CorrelationId={CorrelationId}",
                record.Id,
                record.PumpNumber,
                record.NozzleNumber,
                correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Pre-auth {PreAuthId} expired from DISPENSING but FCC deauthorization failed. CorrelationId={CorrelationId}",
                record.Id,
                correlationId);
        }
    }
}

public sealed class PreAuthExpiryWorkerOptions
{
    public const string SectionName = "PreAuthExpiryWorker";

    public int PollIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 500;
    public int ErrorDelaySeconds { get; set; } = 30;
}
