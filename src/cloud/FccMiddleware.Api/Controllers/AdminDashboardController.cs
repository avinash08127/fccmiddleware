using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Persistence.Configurations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/admin/dashboard")]
[Authorize(Policy = "PortalUser")]
public sealed class AdminDashboardController : PortalControllerBase
{
    private static readonly TimeSpan OfflineGracePeriod = TimeSpan.FromMinutes(5);

    private readonly FccMiddlewareDbContext _db;
    private readonly PortalAccessResolver _accessResolver;

    public AdminDashboardController(FccMiddlewareDbContext db, PortalAccessResolver accessResolver)
    {
        _db = db;
        _accessResolver = accessResolver;
    }

    [HttpGet("summary")]
    [ProducesResponseType(typeof(DashboardSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary([FromQuery] Guid? legalEntityId = null, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (legalEntityId.HasValue && !access.CanAccess(legalEntityId.Value))
        {
            return Forbid();
        }

        var scopedIds = legalEntityId.HasValue
            ? new[] { legalEntityId.Value }
            : access.AllowAllLegalEntities
                ? null
                : access.ScopedLegalEntityIds.ToArray();

        var settings = await LoadSettingsAsync(cancellationToken);
        var staleThresholdMinutes = settings.GlobalDefaults.Tolerance.StalePendingThresholdDays * 24 * 60;
        var now = DateTimeOffset.UtcNow;
        var transactionsWindowStart = now.AddHours(-24);
        var healthWindowStart = now.AddMinutes(-15);
        var staleCutoff = now.AddMinutes(-staleThresholdMinutes);
        var previousStaleWindowStart = staleCutoff.AddMinutes(-staleThresholdMinutes);

        var transactions = await FilterByLegalEntity(_db.Transactions.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .Where(item => item.CreatedAt >= transactionsWindowStart)
            .ToListAsync(cancellationToken);

        var recentTransactions = transactions.Where(item => item.CreatedAt >= healthWindowStart).ToList();
        var agents = await FilterByLegalEntity(_db.AgentRegistrations.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .Where(item => item.IsActive)
            .ToListAsync(cancellationToken);
        var telemetry = await FilterByLegalEntity(_db.AgentTelemetrySnapshots.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var deadLetters = await FilterByLegalEntity(_db.DeadLetterItems.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .Where(item => item.Status != DeadLetterStatus.RESOLVED && item.Status != DeadLetterStatus.DISCARDED)
            .ToListAsync(cancellationToken);
        var reconciliation = await FilterByLegalEntity(_db.ReconciliationRecords.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var telemetryByDevice = telemetry.ToDictionary(item => item.DeviceId);
        var offlineAgents = agents
            .Select(agent =>
            {
                telemetryByDevice.TryGetValue(agent.Id, out var snapshot);
                var state = snapshot?.ConnectivityState ?? ConnectivityState.FULLY_OFFLINE;
                var offline = state == ConnectivityState.FULLY_OFFLINE
                              || !agent.LastSeenAt.HasValue
                              || now - agent.LastSeenAt.Value > OfflineGracePeriod;
                return new { Agent = agent, Snapshot = snapshot, State = state, Offline = offline };
            })
            .Where(item => item.Offline)
            .OrderBy(item => item.Agent.LastSeenAt)
            .Take(10)
            .Select(item => new OfflineAgentItemDto
            {
                DeviceId = item.Agent.Id,
                SiteCode = item.Agent.SiteCode,
                LastSeenAt = item.Agent.LastSeenAt,
                ConnectivityState = item.State.ToString()
            })
            .ToList();

        var onlineCount = agents.Count(agent =>
        {
            telemetryByDevice.TryGetValue(agent.Id, out var snapshot);
            return snapshot?.ConnectivityState == ConnectivityState.FULLY_ONLINE
                   && agent.LastSeenAt.HasValue
                   && now - agent.LastSeenAt.Value <= OfflineGracePeriod;
        });

        var offlineCount = offlineAgents.Count;
        var degradedCount = Math.Max(agents.Count - onlineCount - offlineCount, 0);

        var currentStaleCount = await CountStaleTransactionsAsync(scopedIds, staleCutoff, cancellationToken);
        var previousStaleCount = await FilterByLegalEntity(_db.Transactions.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .CountAsync(item =>
                item.Status == TransactionStatus.PENDING
                && item.CreatedAt >= previousStaleWindowStart
                && item.CreatedAt < staleCutoff,
                cancellationToken);

        var summary = new DashboardSummaryDto
        {
            TransactionVolume = new TransactionVolumeDataDto
            {
                HourlyBuckets = Enumerable.Range(0, 24)
                    .Select(index => transactionsWindowStart.AddHours(index))
                    .Select(hour =>
                    {
                        var hourEnd = hour.AddHours(1);
                        var bucketRows = transactions.Where(item => item.CreatedAt >= hour && item.CreatedAt < hourEnd).ToList();
                        return new TransactionVolumeHourlyBucketDto
                        {
                            Hour = hour,
                            Total = bucketRows.Count,
                            BySource = new TransactionVolumeBySourceDto
                            {
                                FccPush = bucketRows.Count(item => item.IngestionSource == IngestionSource.FCC_PUSH),
                                EdgeUpload = bucketRows.Count(item => item.IngestionSource == IngestionSource.EDGE_UPLOAD),
                                CloudPull = bucketRows.Count(item => item.IngestionSource == IngestionSource.CLOUD_PULL)
                            }
                        };
                    })
                    .ToList()
            },
            IngestionHealth = new IngestionHealthDataDto
            {
                TransactionsPerMinute = Math.Round(recentTransactions.Count / 15m, 2),
                SuccessRate = recentTransactions.Count + deadLetters.Count == 0
                    ? 1m
                    : Math.Round(recentTransactions.Count / (decimal)(recentTransactions.Count + deadLetters.Count), 2),
                ErrorRate = recentTransactions.Count + deadLetters.Count == 0
                    ? 0m
                    : Math.Round(deadLetters.Count / (decimal)(recentTransactions.Count + deadLetters.Count), 2),
                LatencyP95Ms = 0,
                DlqDepth = deadLetters.Count,
                PeriodMinutes = 15
            },
            AgentStatus = new AgentStatusSummaryDataDto
            {
                TotalAgents = agents.Count,
                Online = onlineCount,
                Degraded = degradedCount,
                Offline = offlineCount,
                OfflineAgents = offlineAgents
            },
            Reconciliation = new ReconciliationSummaryDataDto
            {
                PendingExceptions = reconciliation.Count(item => item.Status is ReconciliationStatus.UNMATCHED or ReconciliationStatus.VARIANCE_FLAGGED or ReconciliationStatus.REVIEW_FUZZY_MATCH),
                AutoApproved = reconciliation.Count(item => item.Status is ReconciliationStatus.MATCHED or ReconciliationStatus.VARIANCE_WITHIN_TOLERANCE or ReconciliationStatus.APPROVED),
                Flagged = reconciliation.Count(item => item.Status is ReconciliationStatus.VARIANCE_FLAGGED or ReconciliationStatus.REVIEW_FUZZY_MATCH),
                LastUpdatedAt = reconciliation.Count == 0 ? now : reconciliation.Max(item => item.UpdatedAt)
            },
            StaleTransactions = new StaleTransactionsDataDto
            {
                Count = currentStaleCount,
                Trend = currentStaleCount > previousStaleCount ? "up" : currentStaleCount < previousStaleCount ? "down" : "stable",
                ThresholdMinutes = staleThresholdMinutes
            },
            GeneratedAt = now
        };

        return Ok(summary);
    }

    [HttpGet("alerts")]
    [ProducesResponseType(typeof(DashboardAlertsResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAlerts([FromQuery] Guid? legalEntityId = null, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (legalEntityId.HasValue && !access.CanAccess(legalEntityId.Value))
        {
            return Forbid();
        }

        var scopedIds = legalEntityId.HasValue
            ? new[] { legalEntityId.Value }
            : access.AllowAllLegalEntities
                ? null
                : access.ScopedLegalEntityIds.ToArray();

        var settings = await LoadSettingsAsync(cancellationToken);
        var thresholds = settings.Alerts.Thresholds.ToDictionary(item => item.AlertKey, StringComparer.OrdinalIgnoreCase);
        var alerts = new List<DashboardAlertDto>();
        var now = DateTimeOffset.UtcNow;

        var agents = await FilterByLegalEntity(_db.AgentRegistrations.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .Where(item => item.IsActive)
            .ToListAsync(cancellationToken);

        if (thresholds.TryGetValue("offline_agents_hours", out var offlineThreshold))
        {
            foreach (var agent in agents.Where(item => !item.LastSeenAt.HasValue || now - item.LastSeenAt.Value >= TimeSpan.FromHours((double)offlineThreshold.Threshold)).Take(10))
            {
                alerts.Add(new DashboardAlertDto
                {
                    Id = $"offline-{agent.Id}",
                    Type = "connectivity",
                    Severity = "critical",
                    Message = $"Agent at site {agent.SiteCode} has been offline beyond the configured threshold.",
                    SiteCode = agent.SiteCode,
                    LegalEntityId = agent.LegalEntityId,
                    CreatedAt = agent.LastSeenAt ?? agent.RegisteredAt
                });
            }
        }

        var deadLetterCount = await FilterByLegalEntity(_db.DeadLetterItems.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .CountAsync(item => item.Status != DeadLetterStatus.RESOLVED && item.Status != DeadLetterStatus.DISCARDED, cancellationToken);

        if (thresholds.TryGetValue("dlq_depth", out var dlqThreshold) && deadLetterCount >= dlqThreshold.Threshold)
        {
            alerts.Add(new DashboardAlertDto
            {
                Id = "dlq-depth",
                Type = "dlq",
                Severity = "warning",
                Message = $"Dead-letter queue depth is {deadLetterCount}.",
                SiteCode = null,
                LegalEntityId = legalEntityId,
                CreatedAt = now
            });
        }

        var staleCount = await CountStaleTransactionsAsync(scopedIds, now.AddDays(-settings.GlobalDefaults.Tolerance.StalePendingThresholdDays), cancellationToken);
        if (thresholds.TryGetValue("stale_transactions", out var staleThreshold) && staleCount >= staleThreshold.Threshold)
        {
            alerts.Add(new DashboardAlertDto
            {
                Id = "stale-transactions",
                Type = "stale_data",
                Severity = "warning",
                Message = $"{staleCount} pending transactions are older than the stale threshold.",
                SiteCode = null,
                LegalEntityId = legalEntityId,
                CreatedAt = now
            });
        }

        var reconciliationExceptions = await FilterByLegalEntity(_db.ReconciliationRecords.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .CountAsync(
                item => item.Status == ReconciliationStatus.UNMATCHED
                        || item.Status == ReconciliationStatus.VARIANCE_FLAGGED
                        || item.Status == ReconciliationStatus.REVIEW_FUZZY_MATCH,
                cancellationToken);

        if (thresholds.TryGetValue("reconciliation_exceptions", out var reconciliationThreshold)
            && reconciliationExceptions >= reconciliationThreshold.Threshold)
        {
            alerts.Add(new DashboardAlertDto
            {
                Id = "reconciliation-exceptions",
                Type = "reconciliation",
                Severity = "warning",
                Message = $"{reconciliationExceptions} reconciliation exceptions need review.",
                SiteCode = null,
                LegalEntityId = legalEntityId,
                CreatedAt = now
            });
        }

        return Ok(new DashboardAlertsResponseDto
        {
            Alerts = alerts.OrderByDescending(item => item.CreatedAt).ToList(),
            TotalCount = alerts.Count
        });
    }

    private async Task<int> CountStaleTransactionsAsync(
        IReadOnlyCollection<Guid>? scopedIds,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken) =>
        await FilterByLegalEntity(_db.Transactions.IgnoreQueryFilters(), scopedIds)
            .AsNoTracking()
            .CountAsync(item => item.Status == TransactionStatus.PENDING && item.CreatedAt <= cutoff, cancellationToken);

    private async Task<SystemSettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _db.PortalSettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == PortalSettingsConfiguration.SingletonId, cancellationToken);

        if (settings is null)
        {
            return new SystemSettingsDto
            {
                GlobalDefaults = new GlobalDefaultsDto
                {
                    Tolerance = new ToleranceDefaultsDto
                    {
                        AmountTolerancePercent = 5,
                        AmountToleranceAbsoluteMinorUnits = 500,
                        TimeWindowMinutes = 60,
                        StalePendingThresholdDays = 7
                    },
                    Retention = new RetentionDefaultsDto
                    {
                        ArchiveRetentionMonths = 84,
                        OutboxCleanupDays = 7,
                        RawPayloadRetentionDays = 30,
                        AuditEventRetentionDays = 90,
                        DeadLetterRetentionDays = 30
                    }
                },
                LegalEntityOverrides = Array.Empty<LegalEntityOverrideDto>(),
                Alerts = new AlertConfigurationDto
                {
                    Thresholds = new[]
                    {
                        new AlertThresholdDto { AlertKey = "offline_agents_hours", Label = "Edge agent offline", Threshold = 2, Unit = "hours", EvaluationWindowMinutes = 120 },
                        new AlertThresholdDto { AlertKey = "dlq_depth", Label = "Dead-letter depth", Threshold = 1, Unit = "items", EvaluationWindowMinutes = 15 },
                        new AlertThresholdDto { AlertKey = "stale_transactions", Label = "Stale pending transactions", Threshold = 10, Unit = "items", EvaluationWindowMinutes = 60 },
                        new AlertThresholdDto { AlertKey = "reconciliation_exceptions", Label = "Reconciliation exceptions", Threshold = 10, Unit = "items", EvaluationWindowMinutes = 60 }
                    },
                    EmailRecipientsHigh = Array.Empty<string>(),
                    EmailRecipientsCritical = Array.Empty<string>(),
                    RenotifyIntervalHours = 4,
                    AutoResolveHealthyCount = 3
                },
                UpdatedAt = null,
                UpdatedBy = null
            };
        }

        return new SystemSettingsDto
        {
            GlobalDefaults = JsonSerializer.Deserialize<GlobalDefaultsDto>(settings.GlobalDefaultsJson, PortalJson.SerializerOptions)!,
            LegalEntityOverrides = Array.Empty<LegalEntityOverrideDto>(),
            Alerts = JsonSerializer.Deserialize<AlertConfigurationDto>(settings.AlertConfigurationJson, PortalJson.SerializerOptions)!,
            UpdatedAt = settings.UpdatedAt,
            UpdatedBy = settings.UpdatedBy
        };
    }

    private static IQueryable<T> FilterByLegalEntity<T>(IQueryable<T> query, IReadOnlyCollection<Guid>? scopedIds)
        where T : class
    {
        if (scopedIds is null)
        {
            return query;
        }

        return typeof(T) switch
        {
            var type when type == typeof(Transaction) =>
                (IQueryable<T>)((IQueryable<Transaction>)query).Where(item => scopedIds.Contains(item.LegalEntityId)),
            var type when type == typeof(AgentRegistration) =>
                (IQueryable<T>)((IQueryable<AgentRegistration>)query).Where(item => scopedIds.Contains(item.LegalEntityId)),
            var type when type == typeof(AgentTelemetrySnapshot) =>
                (IQueryable<T>)((IQueryable<AgentTelemetrySnapshot>)query).Where(item => scopedIds.Contains(item.LegalEntityId)),
            var type when type == typeof(DeadLetterItem) =>
                (IQueryable<T>)((IQueryable<DeadLetterItem>)query).Where(item => scopedIds.Contains(item.LegalEntityId)),
            var type when type == typeof(ReconciliationRecord) =>
                (IQueryable<T>)((IQueryable<ReconciliationRecord>)query).Where(item => scopedIds.Contains(item.LegalEntityId)),
            _ => query
        };
    }
}
