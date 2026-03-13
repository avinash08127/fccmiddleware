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

    /// <inheritdoc />
    public async Task<UploadTransactionBatchResult?> GetBatchResultAsync(
        string uploadBatchId,
        CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var cacheKey = BuildBatchCacheKey(uploadBatchId);
            var cached = await db.StringGetAsync(cacheKey);
            if (cached.HasValue)
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<UploadTransactionBatchResult>(cached.ToString());
                if (result is not null)
                {
                    _logger.LogDebug("Batch cache hit for batchId={BatchId}", uploadBatchId);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: caller will process the batch normally
            _logger.LogWarning(ex, "Redis batch cache unavailable for batchId={BatchId}", uploadBatchId);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task SetBatchResultAsync(
        string uploadBatchId,
        UploadTransactionBatchResult result,
        CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var cacheKey = BuildBatchCacheKey(uploadBatchId);
            var json = System.Text.Json.JsonSerializer.Serialize(result);
            await db.StringSetAsync(cacheKey, json, BatchResultTtl);
        }
        catch (Exception ex)
        {
            // Non-fatal: worst case is the edge retries and cloud re-processes (dedup handles it)
            _logger.LogWarning(ex, "Failed to cache batch result for batchId={BatchId}", uploadBatchId);
        }
    }

    private static string BuildCacheKey(string fccTransactionId, string siteCode) =>
        $"dedup:{siteCode}:{fccTransactionId}";

    private static string BuildBatchCacheKey(string uploadBatchId) =>
        $"batch:{uploadBatchId}";

    /// <summary>
    /// Batch result cache TTL. 10 minutes is sufficient for edge retry windows
    /// while keeping Redis memory bounded.
    /// </summary>
    private static readonly TimeSpan BatchResultTtl = TimeSpan.FromMinutes(10);
}
