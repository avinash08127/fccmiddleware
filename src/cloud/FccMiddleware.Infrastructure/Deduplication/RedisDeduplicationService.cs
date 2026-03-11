using FccMiddleware.Application.Ingestion;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FccMiddleware.Infrastructure.Deduplication;

/// <summary>
/// Two-tier deduplication: Redis cache (fast path) with PostgreSQL fallback.
/// Cache key: "dedup:{siteCode}:{fccTransactionId}" → transaction UUID as string.
/// </summary>
public sealed class RedisDeduplicationService : IDeduplicationService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDeduplicationDbContext _dbContext;
    private readonly ILogger<RedisDeduplicationService> _logger;

    public RedisDeduplicationService(
        IConnectionMultiplexer redis,
        IDeduplicationDbContext dbContext,
        ILogger<RedisDeduplicationService> logger)
    {
        _redis = redis;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<Guid?> FindExistingAsync(
        string fccTransactionId,
        string siteCode,
        CancellationToken ct = default)
    {
        // Fast path: Redis cache
        try
        {
            var db = _redis.GetDatabase();
            var cacheKey = BuildCacheKey(fccTransactionId, siteCode);
            var cached = await db.StringGetAsync(cacheKey);
            if (cached.HasValue && Guid.TryParse(cached.ToString(), out var cachedId))
            {
                _logger.LogDebug("Dedup cache hit for {FccTransactionId}/{SiteCode}", fccTransactionId, siteCode);
                return cachedId;
            }
        }
        catch (Exception ex)
        {
            // Redis unavailable — fall through to DB check
            _logger.LogWarning(ex, "Redis dedup cache unavailable; falling back to DB for {FccTransactionId}", fccTransactionId);
        }

        // Fallback: PostgreSQL within the dedup window
        return await _dbContext.FindTransactionIdByDedupKeyAsync(fccTransactionId, siteCode, ct);
    }

    /// <inheritdoc />
    public async Task SetCacheAsync(
        string fccTransactionId,
        string siteCode,
        Guid transactionId,
        int dedupWindowDays,
        CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var cacheKey = BuildCacheKey(fccTransactionId, siteCode);
            await db.StringSetAsync(
                cacheKey,
                transactionId.ToString(),
                TimeSpan.FromDays(dedupWindowDays));
        }
        catch (Exception ex)
        {
            // Non-fatal: DB is the source of truth
            _logger.LogWarning(ex, "Failed to set dedup cache for {FccTransactionId}", fccTransactionId);
        }
    }

    private static string BuildCacheKey(string fccTransactionId, string siteCode) =>
        $"dedup:{siteCode}:{fccTransactionId}";
}
