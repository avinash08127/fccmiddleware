using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Pump;

/// <summary>
/// Enforces per-pump transaction limits by blocking/unblocking pumps via the FCC adapter.
/// Ported from legacy ForecourtClient.CheckAndApplyPumpLimitAsync() and
/// CheckAndApplyPumpLimitAsync_IsAllowed().
/// </summary>
public sealed class PumpLimitEnforcer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PumpLimitEnforcer> _logger;

    public PumpLimitEnforcer(
        IServiceScopeFactory scopeFactory,
        ILogger<PumpLimitEnforcer> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Check all pump limits and apply block/unblock as needed.
    /// fpId=0 means check all pumps.
    /// Ported from legacy CheckAndApplyPumpLimitAsync().
    /// </summary>
    public async Task EnforceLimitsAsync(IFccPumpControl pumpControl, int fpId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

            var limits = fpId == 0
                ? await db.Set<PumpLimit>().ToListAsync(ct)
                : await db.Set<PumpLimit>().Where(l => l.FpId == fpId).ToListAsync(ct);

            foreach (var limit in limits)
            {
                if (limit.CurrentCount >= limit.MaxLimit && limit.Status != "blocked")
                {
                    _logger.LogWarning("LIMIT REACHED — Blocking FP={FpId} (count={Count} >= max={Max})",
                        limit.FpId, limit.CurrentCount, limit.MaxLimit);

                    var result = await pumpControl.EmergencyStopAsync(limit.FpId, ct);
                    if (result.Success)
                    {
                        limit.Status = "blocked";
                        limit.UpdatedAt = DateTimeOffset.UtcNow;
                        await RecordBlockHistoryAsync(db, limit.FpId, "Blocked", "Middleware",
                            $"Transaction limit reached: {limit.CurrentCount}/{limit.MaxLimit}", ct);
                    }
                }
                else if (limit.CurrentCount < limit.MaxLimit && limit.Status == "blocked")
                {
                    _logger.LogInformation("LIMIT AVAILABLE — Unblocking FP={FpId} (count={Count} < max={Max})",
                        limit.FpId, limit.CurrentCount, limit.MaxLimit);

                    var result = await pumpControl.CancelEmergencyStopAsync(limit.FpId, ct);
                    if (result.Success)
                    {
                        limit.Status = "active";
                        limit.UpdatedAt = DateTimeOffset.UtcNow;
                        await RecordBlockHistoryAsync(db, limit.FpId, "Unblock", "Middleware",
                            $"Transaction limit available: {limit.CurrentCount}/{limit.MaxLimit}", ct);
                    }
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enforce pump limits for FpId={FpId}", fpId);
        }
    }

    /// <summary>
    /// Override-based limit check with IsAllowed flag.
    /// Ported from legacy CheckAndApplyPumpLimitAsync_IsAllowed().
    /// When IsAllowed=true and pump is currently blocked, unblock it.
    /// When IsAllowed=false, block the pump regardless of count.
    /// </summary>
    public async Task EnforceLimitsWithOverrideAsync(
        IFccPumpControl pumpControl, int fpId, bool isAllowedOverride, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

            var limits = fpId == 0
                ? await db.Set<PumpLimit>().ToListAsync(ct)
                : await db.Set<PumpLimit>().Where(l => l.FpId == fpId).ToListAsync(ct);

            foreach (var limit in limits)
            {
                limit.IsAllowed = isAllowedOverride;

                if (isAllowedOverride && limit.CurrentCount < limit.MaxLimit && limit.Status == "blocked")
                {
                    _logger.LogInformation("OVERRIDE — Unblocking FP={FpId} (IsAllowed=true)", limit.FpId);

                    var result = await pumpControl.CancelEmergencyStopAsync(limit.FpId, ct);
                    if (result.Success)
                    {
                        limit.Status = "active";
                        limit.UpdatedAt = DateTimeOffset.UtcNow;
                        await RecordBlockHistoryAsync(db, limit.FpId, "Unblock", "Manager",
                            "Manual override: IsAllowed=true", ct);
                    }
                }
                else if (!isAllowedOverride && limit.Status != "blocked")
                {
                    _logger.LogWarning("OVERRIDE — Blocking FP={FpId} (IsAllowed=false)", limit.FpId);

                    var result = await pumpControl.EmergencyStopAsync(limit.FpId, ct);
                    if (result.Success)
                    {
                        limit.Status = "blocked";
                        limit.UpdatedAt = DateTimeOffset.UtcNow;
                        await RecordBlockHistoryAsync(db, limit.FpId, "Blocked", "Manager",
                            "Manual override: IsAllowed=false", ct);
                    }
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enforce pump limits with override for FpId={FpId}", fpId);
        }
    }

    /// <summary>
    /// Reset transaction count for a pump and re-evaluate limits.
    /// Ported from legacy TransactionService.FpLimitReset().
    /// </summary>
    public async Task ResetLimitAsync(
        IFccPumpControl pumpControl, int fpId, int? newLimit, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

            var limit = await db.Set<PumpLimit>().FirstOrDefaultAsync(l => l.FpId == fpId, ct);
            if (limit is null) return;

            limit.CurrentCount = 0;
            if (newLimit.HasValue)
                limit.MaxLimit = newLimit.Value;
            limit.UpdatedAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync(ct);

            // Re-evaluate limits after reset
            await EnforceLimitsAsync(pumpControl, fpId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset pump limit for FpId={FpId}", fpId);
        }
    }

    /// <summary>
    /// Upsert an attendant pump count record.
    /// Ported from legacy UpsertAttendantPumpCountAsync().
    /// </summary>
    public async Task UpsertAttendantPumpCountAsync(
        string sessionId, string empTagNo, int pumpNumber, int newMaxTransaction, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

            var existing = await db.Set<AttendantPumpCount>()
                .FirstOrDefaultAsync(a => a.SessionId == sessionId && a.PumpNumber == pumpNumber, ct);

            if (existing is not null)
            {
                existing.MaxTransactions = newMaxTransaction;
                existing.EmpTagNo = empTagNo;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.Set<AttendantPumpCount>().Add(new AttendantPumpCount
                {
                    SessionId = sessionId,
                    EmpTagNo = empTagNo,
                    PumpNumber = pumpNumber,
                    MaxTransactions = newMaxTransaction,
                });
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert attendant pump count for pump {Pump}", pumpNumber);
        }
    }

    private static async Task RecordBlockHistoryAsync(
        AgentDbContext db, int fpId, string actionType, string source, string note, CancellationToken ct)
    {
        db.Set<PumpBlockHistory>().Add(new PumpBlockHistory
        {
            FpId = fpId,
            ActionType = actionType,
            Source = source,
            Note = note,
            Timestamp = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
