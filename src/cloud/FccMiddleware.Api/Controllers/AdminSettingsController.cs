using FccMiddleware.Api.Portal;
using FccMiddleware.Contracts.Common;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Persistence.Configurations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FccMiddleware.Api.Controllers;

[ApiController]
[Route("api/v1/admin/settings")]
[Authorize(Policy = "PortalAdminWrite")]
public sealed class AdminSettingsController : PortalControllerBase
{
    private readonly FccMiddlewareDbContext _db;
    private readonly PortalAccessResolver _accessResolver;

    public AdminSettingsController(FccMiddlewareDbContext db, PortalAccessResolver accessResolver)
    {
        _db = db;
        _accessResolver = accessResolver;
    }

    [HttpGet]
    [ProducesResponseType(typeof(SystemSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSettings(CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var settings = await EnsureSettingsAsync(cancellationToken);
        return Ok(await BuildResponseAsync(settings, access, cancellationToken));
    }

    [HttpPut("global-defaults")]
    [ProducesResponseType(typeof(SystemSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateGlobalDefaults(
        [FromBody] UpdateGlobalDefaultsRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var settings = await EnsureSettingsAsync(cancellationToken);
        var current = JsonSerializer.Deserialize<GlobalDefaultsDto>(settings.GlobalDefaultsJson, PortalJson.SerializerOptions)!;

        settings.GlobalDefaultsJson = JsonSerializer.Serialize(new GlobalDefaultsDto
        {
            Tolerance = new ToleranceDefaultsDto
            {
                AmountTolerancePercent = request.Tolerance.AmountTolerancePercent ?? current.Tolerance.AmountTolerancePercent,
                AmountToleranceAbsoluteMinorUnits = request.Tolerance.AmountToleranceAbsoluteMinorUnits ?? current.Tolerance.AmountToleranceAbsoluteMinorUnits,
                TimeWindowMinutes = request.Tolerance.TimeWindowMinutes ?? current.Tolerance.TimeWindowMinutes,
                StalePendingThresholdDays = request.Tolerance.StalePendingThresholdDays ?? current.Tolerance.StalePendingThresholdDays
            },
            Retention = new RetentionDefaultsDto
            {
                ArchiveRetentionMonths = request.Retention.ArchiveRetentionMonths ?? current.Retention.ArchiveRetentionMonths,
                OutboxCleanupDays = request.Retention.OutboxCleanupDays ?? current.Retention.OutboxCleanupDays,
                RawPayloadRetentionDays = request.Retention.RawPayloadRetentionDays ?? current.Retention.RawPayloadRetentionDays,
                AuditEventRetentionDays = request.Retention.AuditEventRetentionDays ?? current.Retention.AuditEventRetentionDays,
                DeadLetterRetentionDays = request.Retention.DeadLetterRetentionDays ?? current.Retention.DeadLetterRetentionDays
            }
        }, PortalJson.SerializerOptions);

        UpdateAuditFields(settings);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(await BuildResponseAsync(settings, access, cancellationToken));
    }

    [HttpPut("overrides/{legalEntityId:guid}")]
    [ProducesResponseType(typeof(SystemSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpsertOverride(
        Guid legalEntityId,
        [FromBody] UpsertLegalEntityOverrideRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (legalEntityId != request.LegalEntityId)
        {
            return BadRequest(BuildError("VALIDATION.LEGAL_ENTITY_MISMATCH", "Path legalEntityId must match request body."));
        }

        if (!access.CanAccess(legalEntityId))
        {
            return Forbid();
        }

        var legalEntityExists = await _db.LegalEntities
            .IgnoreQueryFilters()
            .AnyAsync(item => item.Id == legalEntityId, cancellationToken);

        if (!legalEntityExists)
        {
            return NotFound(BuildError("NOT_FOUND.LEGAL_ENTITY", "Legal entity was not found."));
        }

        var overrideRow = await _db.LegalEntitySettingsOverrides
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.LegalEntityId == legalEntityId, cancellationToken);

        if (overrideRow is null)
        {
            overrideRow = new LegalEntitySettingsOverride
            {
                LegalEntityId = legalEntityId,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _db.LegalEntitySettingsOverrides.Add(overrideRow);
        }

        overrideRow.AmountTolerancePercent = request.AmountTolerancePercent;
        overrideRow.AmountToleranceAbsoluteMinorUnits = request.AmountToleranceAbsoluteMinorUnits;
        overrideRow.TimeWindowMinutes = request.TimeWindowMinutes;
        overrideRow.StalePendingThresholdDays = request.StalePendingThresholdDays;
        overrideRow.UpdatedAt = DateTimeOffset.UtcNow;
        overrideRow.UpdatedBy = _accessResolver.ResolveUserId(User);

        var settings = await EnsureSettingsAsync(cancellationToken);
        UpdateAuditFields(settings);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(await BuildResponseAsync(settings, access, cancellationToken));
    }

    [HttpDelete("overrides/{legalEntityId:guid}")]
    [ProducesResponseType(typeof(SystemSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteOverride(Guid legalEntityId, CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        if (!access.CanAccess(legalEntityId))
        {
            return Forbid();
        }

        var overrideRow = await _db.LegalEntitySettingsOverrides
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.LegalEntityId == legalEntityId, cancellationToken);

        if (overrideRow is not null)
        {
            _db.LegalEntitySettingsOverrides.Remove(overrideRow);
        }

        var settings = await EnsureSettingsAsync(cancellationToken);
        UpdateAuditFields(settings);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(await BuildResponseAsync(settings, access, cancellationToken));
    }

    [HttpPut("alerts")]
    [ProducesResponseType(typeof(SystemSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateAlerts(
        [FromBody] UpdateAlertConfigurationRequestDto request,
        CancellationToken cancellationToken = default)
    {
        var access = _accessResolver.Resolve(User);
        if (!access.IsValid)
        {
            return Unauthorized();
        }

        var settings = await EnsureSettingsAsync(cancellationToken);
        var current = JsonSerializer.Deserialize<AlertConfigurationDto>(settings.AlertConfigurationJson, PortalJson.SerializerOptions)!;
        var existingByKey = current.Thresholds.ToDictionary(item => item.AlertKey, StringComparer.OrdinalIgnoreCase);

        settings.AlertConfigurationJson = JsonSerializer.Serialize(new AlertConfigurationDto
        {
            Thresholds = request.Thresholds.Select(item =>
            {
                existingByKey.TryGetValue(item.AlertKey, out var existing);
                return new AlertThresholdDto
                {
                    AlertKey = item.AlertKey,
                    Label = existing?.Label ?? item.AlertKey,
                    Threshold = item.Threshold,
                    Unit = existing?.Unit ?? "items",
                    EvaluationWindowMinutes = item.EvaluationWindowMinutes
                };
            }).ToList(),
            EmailRecipientsHigh = request.EmailRecipientsHigh,
            EmailRecipientsCritical = request.EmailRecipientsCritical,
            RenotifyIntervalHours = request.RenotifyIntervalHours,
            AutoResolveHealthyCount = request.AutoResolveHealthyCount
        }, PortalJson.SerializerOptions);

        UpdateAuditFields(settings);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(await BuildResponseAsync(settings, access, cancellationToken));
    }

    private async Task<PortalSettings> EnsureSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await _db.PortalSettings
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(item => item.Id == PortalSettingsConfiguration.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        settings = new PortalSettings
        {
            Id = PortalSettingsConfiguration.SingletonId,
            GlobalDefaultsJson = """
                {"tolerance":{"amountTolerancePercent":5.0,"amountToleranceAbsoluteMinorUnits":500,"timeWindowMinutes":60,"stalePendingThresholdDays":7},"retention":{"archiveRetentionMonths":84,"outboxCleanupDays":7,"rawPayloadRetentionDays":30,"auditEventRetentionDays":90,"deadLetterRetentionDays":30}}
                """,
            AlertConfigurationJson = """
                {"thresholds":[{"alertKey":"offline_agents_hours","label":"Edge agent offline","threshold":2,"unit":"hours","evaluationWindowMinutes":120},{"alertKey":"dlq_depth","label":"Dead-letter depth","threshold":1,"unit":"items","evaluationWindowMinutes":15},{"alertKey":"stale_transactions","label":"Stale pending transactions","threshold":10,"unit":"items","evaluationWindowMinutes":60},{"alertKey":"reconciliation_exceptions","label":"Reconciliation exceptions","threshold":10,"unit":"items","evaluationWindowMinutes":60}],"emailRecipientsHigh":[],"emailRecipientsCritical":[],"renotifyIntervalHours":4,"autoResolveHealthyCount":3}
                """,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = _accessResolver.ResolveUserId(User)
        };

        _db.PortalSettings.Add(settings);
        await _db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    private async Task<SystemSettingsDto> BuildResponseAsync(
        PortalSettings settings,
        PortalAccess access,
        CancellationToken cancellationToken)
    {
        var globalDefaults = JsonSerializer.Deserialize<GlobalDefaultsDto>(settings.GlobalDefaultsJson, PortalJson.SerializerOptions)!;
        var alerts = JsonSerializer.Deserialize<AlertConfigurationDto>(settings.AlertConfigurationJson, PortalJson.SerializerOptions)!;

        var overrideQuery = _db.LegalEntitySettingsOverrides
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(item => item.LegalEntity)
            .AsQueryable();

        if (!access.AllowAllLegalEntities)
        {
            overrideQuery = overrideQuery.Where(item => access.ScopedLegalEntityIds.Contains(item.LegalEntityId));
        }

        var overrides = await overrideQuery
            .OrderBy(item => item.LegalEntity.Name)
            .Select(item => new LegalEntityOverrideDto
            {
                LegalEntityId = item.LegalEntityId,
                LegalEntityName = item.LegalEntity.Name,
                LegalEntityCode = item.LegalEntity.CountryCode,
                AmountTolerancePercent = item.AmountTolerancePercent,
                AmountToleranceAbsoluteMinorUnits = item.AmountToleranceAbsoluteMinorUnits,
                TimeWindowMinutes = item.TimeWindowMinutes,
                StalePendingThresholdDays = item.StalePendingThresholdDays
            })
            .ToListAsync(cancellationToken);

        return new SystemSettingsDto
        {
            GlobalDefaults = globalDefaults,
            LegalEntityOverrides = overrides,
            Alerts = alerts,
            UpdatedAt = settings.UpdatedAt,
            UpdatedBy = settings.UpdatedBy
        };
    }

    private void UpdateAuditFields(PortalSettings settings)
    {
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.UpdatedBy = _accessResolver.ResolveUserId(User);
    }
}
