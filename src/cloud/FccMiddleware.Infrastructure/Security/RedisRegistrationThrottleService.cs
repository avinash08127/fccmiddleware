using FccMiddleware.Application.Registration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace FccMiddleware.Infrastructure.Security;

/// <summary>
/// Redis-backed IP throttle for the device registration endpoint.
/// Blocks an IP for <see cref="BlockDuration"/> after <see cref="MaxFailedAttempts"/>
/// consecutive failed attempts within the same window.
/// </summary>
public sealed class RedisRegistrationThrottleService : IRegistrationThrottleService
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);
    private const string KeyPrefix = "reg-block:";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisRegistrationThrottleService> _logger;

    public RedisRegistrationThrottleService(
        IConnectionMultiplexer redis,
        ILogger<RedisRegistrationThrottleService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<bool> IsBlockedAsync(string ipAddress, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var count = await db.StringGetAsync(KeyPrefix + ipAddress);
            return count.HasValue && (long)count >= MaxFailedAttempts;
        }
        catch (Exception ex)
        {
            // Fail open — do not block legitimate traffic when Redis is down.
            _logger.LogWarning(ex, "Redis unavailable for registration throttle check on {Ip}", ipAddress);
            return false;
        }
    }

    public async Task RecordFailedAttemptAsync(string ipAddress, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            var key = KeyPrefix + ipAddress;
            var count = await db.StringIncrementAsync(key);

            // Set TTL only on the first increment so the window doesn't keep sliding.
            if (count == 1)
                await db.KeyExpireAsync(key, BlockDuration);

            if (count >= MaxFailedAttempts)
                _logger.LogWarning(
                    "IP {Ip} blocked after {Count} failed registration attempts",
                    ipAddress, count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable for recording failed registration attempt from {Ip}",
                ipAddress);
        }
    }

    public async Task ResetAsync(string ipAddress, CancellationToken ct = default)
    {
        try
        {
            var db = _redis.GetDatabase();
            await db.KeyDeleteAsync(KeyPrefix + ipAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Redis unavailable for resetting registration throttle for {Ip}",
                ipAddress);
        }
    }
}
