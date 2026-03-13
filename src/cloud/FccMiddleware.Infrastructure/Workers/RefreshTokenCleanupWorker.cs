using FccMiddleware.Domain.Entities;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Infrastructure.Workers;

/// <summary>
/// OB-P03: Periodically deletes revoked device refresh tokens older than the retention window.
/// Tokens revoked more than 90 days ago cannot trigger reuse detection (they are also expired),
/// so they are safe to purge. This prevents unbounded table growth in device_refresh_tokens.
/// </summary>
public sealed class RefreshTokenCleanupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RefreshTokenCleanupWorker> _logger;
    private readonly RefreshTokenCleanupWorkerOptions _options;

    public RefreshTokenCleanupWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<RefreshTokenCleanupWorker> logger,
        IOptions<RefreshTokenCleanupWorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "RefreshTokenCleanupWorker started. PollInterval={PollInterval}s, BatchSize={BatchSize}, RetentionDays={RetentionDays}",
            _options.PollIntervalSeconds,
            _options.BatchSize,
            _options.RetentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await PurgeBatchAsync(stoppingToken);
                if (deleted == 0)
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RefreshTokenCleanupWorker error during batch processing");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorDelaySeconds), stoppingToken);
            }
        }

        _logger.LogInformation("RefreshTokenCleanupWorker stopped");
    }

    internal async Task<int> PurgeBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays);

        var staleTokens = await db.Set<DeviceRefreshToken>()
            .Where(t => t.RevokedAt != null && t.RevokedAt < cutoff)
            .OrderBy(t => t.RevokedAt)
            .Take(_options.BatchSize)
            .ToListAsync(ct);

        if (staleTokens.Count == 0)
            return 0;

        db.Set<DeviceRefreshToken>().RemoveRange(staleTokens);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "RefreshTokenCleanupWorker purged {Count} revoked refresh tokens older than {RetentionDays} days",
            staleTokens.Count,
            _options.RetentionDays);

        return staleTokens.Count;
    }
}

public sealed class RefreshTokenCleanupWorkerOptions
{
    public const string SectionName = "RefreshTokenCleanupWorker";

    /// <summary>
    /// Polling interval in seconds. Defaults to 3600 (1 hour) since cleanup is not time-critical.
    /// </summary>
    public int PollIntervalSeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum number of tokens to delete per batch.
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Number of days to retain revoked tokens. Matches the 90-day refresh token expiry window.
    /// </summary>
    public int RetentionDays { get; set; } = 90;

    public int ErrorDelaySeconds { get; set; } = 30;
}
