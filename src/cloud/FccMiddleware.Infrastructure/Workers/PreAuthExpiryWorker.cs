using FccMiddleware.Application.PreAuth;
using FccMiddleware.Domain.Enums;
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
        var now = DateTimeOffset.UtcNow;

        // Step 1: Identify distinct tenants with expired records (read-only, cross-tenant).
        List<Guid> affectedTenants;
        using (var readScope = _scopeFactory.CreateScope())
        {
            var readDb = readScope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            affectedTenants = await readDb.PreAuthRecords
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p =>
                    (p.Status == PreAuthStatus.PENDING
                  || p.Status == PreAuthStatus.AUTHORIZED
                  || p.Status == PreAuthStatus.DISPENSING)
                 && p.ExpiresAt <= now)
                .Select(p => p.LegalEntityId)
                .Distinct()
                .ToListAsync(ct);
        }

        if (affectedTenants.Count == 0)
            return 0;

        // Step 2: Process each tenant in an isolated scope so a failure in one
        //         tenant's batch (save or deauth) does not affect other tenants.
        var totalExpired = 0;
        foreach (var legalEntityId in affectedTenants)
        {
            try
            {
                var expired = await ExpireTenantBatchAsync(legalEntityId, now, ct);
                totalExpired += expired;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PreAuthExpiryWorker error processing expired pre-auths for LegalEntityId={LegalEntityId}",
                    legalEntityId);
            }
        }

        return totalExpired;
    }

    private async Task<int> ExpireTenantBatchAsync(Guid legalEntityId, DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var eventPublisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        var expiredRecords = await db.PreAuthRecords
            .IgnoreQueryFilters()
            .Where(p =>
                p.LegalEntityId == legalEntityId
             && (p.Status == PreAuthStatus.PENDING
              || p.Status == PreAuthStatus.AUTHORIZED
              || p.Status == PreAuthStatus.DISPENSING)
             && p.ExpiresAt <= now)
            .OrderBy(p => p.ExpiresAt)
            .ThenBy(p => p.Id)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (expiredRecords.Count == 0)
            return 0;

        var dispensingCount = 0;

        foreach (var record in expiredRecords)
        {
            if (record.Status == PreAuthStatus.DISPENSING)
                dispensingCount++;

            record.Transition(PreAuthStatus.EXPIRED);
            record.ExpiredAt ??= now;

            eventPublisher.Publish(PreAuthEventFactory.CreateForStatus(
                record,
                Guid.NewGuid(),
                source: "cloud-preauth"));
        }

        await db.SaveChangesAsync(ct);

        // PA-S05: FCC pump deauthorization is NOT performed from the cloud.
        // Calling FCC devices directly from the cloud would create an outbound network path
        // (cloud → FCC device LAN) that bypasses network segmentation designed for the
        // inbound-only cloud adapter pattern. Instead, the edge agent handles deauthorization
        // locally via its own expiry check (PreAuthHandler.runExpiryCheck) with retry logic.
        // The PreAuthExpired domain event published above signals the state change.
        if (dispensingCount > 0)
        {
            _logger.LogInformation(
                "PreAuthExpiryWorker: {Count} DISPENSING records expired for LegalEntityId={LegalEntityId}. " +
                "FCC pump deauthorization is delegated to the edge agent's local expiry mechanism",
                dispensingCount,
                legalEntityId);
        }

        _logger.LogInformation(
            "PreAuthExpiryWorker expired {Count} pre-auth records for LegalEntityId={LegalEntityId}",
            expiredRecords.Count,
            legalEntityId);

        return expiredRecords.Count;
    }

}

public sealed class PreAuthExpiryWorkerOptions
{
    public const string SectionName = "PreAuthExpiryWorker";

    public int PollIntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 500;
    public int ErrorDelaySeconds { get; set; } = 30;
}
