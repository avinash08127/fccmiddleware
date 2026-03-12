using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.MasterData;

/// <summary>
/// Handles SyncSitesCommand — upserts sites and only soft-deletes absent ones for full snapshots.
/// </summary>
public sealed class SyncSitesHandler : IRequestHandler<SyncSitesCommand, MasterDataSyncResult>
{
    private readonly IMasterDataSyncDbContext _db;
    private readonly ILogger<SyncSitesHandler> _logger;

    public SyncSitesHandler(IMasterDataSyncDbContext db, ILogger<SyncSitesHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MasterDataSyncResult> Handle(SyncSitesCommand command, CancellationToken ct)
    {
        var now        = DateTimeOffset.UtcNow;
        var errors     = new List<string>();
        var incomingIds = command.Items.Select(i => i.Id).ToList();

        var existing = await _db.GetSitesByIdsAsync(incomingIds, ct);
        var byId     = existing.ToDictionary(e => e.Id);

        int upserted = 0, unchanged = 0;

        foreach (var item in command.Items)
        {
            if (!Enum.TryParse<SiteOperatingModel>(item.OperatingModel, out var model))
            {
                errors.Add($"Site {item.Id}: unknown operatingModel '{item.OperatingModel}'.");
                continue;
            }

            if (!Enum.TryParse<FiscalizationMode>(item.FiscalizationMode, true, out var fiscalizationMode))
            {
                errors.Add($"Site {item.Id}: unknown fiscalizationMode '{item.FiscalizationMode}'.");
                continue;
            }

            if (!IsValidConnectivityMode(item.ConnectivityMode))
            {
                errors.Add($"Site {item.Id}: unknown connectivityMode '{item.ConnectivityMode}'.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.CompanyTaxPayerId))
            {
                errors.Add($"Site {item.Id}: companyTaxPayerId is required.");
                continue;
            }

            if (!item.SiteUsesPreAuth.HasValue)
            {
                errors.Add($"Site {item.Id}: siteUsesPreAuth is required.");
                continue;
            }

            if (IsDealerOperated(model)
                && (string.IsNullOrWhiteSpace(item.OperatorName) || string.IsNullOrWhiteSpace(item.OperatorTaxPayerId)))
            {
                errors.Add($"Site {item.Id}: dealer-operated sites require operatorName and operatorTaxPayerId.");
                continue;
            }

            if (byId.TryGetValue(item.Id, out var entity))
            {
                if (HasChanges(entity, item, model, fiscalizationMode))
                {
                    ApplyChanges(entity, item, model, fiscalizationMode, now);
                    upserted++;
                }
                else
                {
                    entity.SyncedAt = now;
                    unchanged++;
                }
            }
            else
            {
                _db.AddSite(CreateNew(item, model, fiscalizationMode, now));
                upserted++;
            }
        }

        int deactivated  = 0;

        if (command.IsFullSnapshot)
        {
            var allActiveIds = await _db.GetActiveSiteIdsAsync(ct);
            var incomingSet  = incomingIds.ToHashSet();
            var toDeactivate = allActiveIds.Where(id => !incomingSet.Contains(id)).ToList();

            if (toDeactivate.Count > 0)
            {
                var deactivateEntities = await _db.GetSitesByIdsAsync(toDeactivate, ct);
                foreach (var entity in deactivateEntities)
                {
                    entity.IsActive       = false;
                    entity.DeactivatedAt  = now;
                    entity.UpdatedAt      = now;
                    deactivated++;
                }
            }
        }

        if (upserted > 0 || deactivated > 0)
        {
            _db.AddOutboxMessage(new OutboxMessage
            {
                EventType = "SitesSynced",
                Payload   = JsonSerializer.Serialize(new { total = command.Items.Count, upserted, deactivated, syncedAt = now }),
                CreatedAt = now
            });

            _logger.LogInformation(
                "Site sync: upserted={Upserted}, unchanged={Unchanged}, deactivated={Deactivated}",
                upserted, unchanged, deactivated);
        }

        await _db.SaveChangesAsync(ct);

        return new MasterDataSyncResult
        {
            UpsertedCount    = upserted,
            UnchangedCount   = unchanged,
            DeactivatedCount = deactivated,
            ErrorCount       = errors.Count,
            Errors           = errors
        };
    }

    private static bool HasChanges(Site e, SiteSyncItem i, SiteOperatingModel model, FiscalizationMode fiscalizationMode) =>
        e.SiteCode              != i.SiteCode ||
        e.LegalEntityId         != i.LegalEntityId ||
        e.SiteName              != i.SiteName ||
        e.OperatingModel        != model ||
        e.ConnectivityMode      != i.ConnectivityMode ||
        e.CompanyTaxPayerId     != i.CompanyTaxPayerId ||
        e.OperatorName          != NormalizeOptional(i.OperatorName) ||
        e.OperatorTaxPayerId    != NormalizeOptional(i.OperatorTaxPayerId) ||
        e.SiteUsesPreAuth       != i.SiteUsesPreAuth.GetValueOrDefault() ||
        e.FiscalizationMode     != fiscalizationMode ||
        e.TaxAuthorityEndpoint  != NormalizeOptional(i.TaxAuthorityEndpoint) ||
        e.RequireCustomerTaxId  != i.RequireCustomerTaxId ||
        e.FiscalReceiptRequired != i.FiscalReceiptRequired ||
        e.OdooSiteId            != NormalizeOptional(i.OdooSiteId) ||
        e.AmountTolerancePercent  != i.AmountTolerancePercent ||
        e.AmountToleranceAbsolute != i.AmountToleranceAbsolute ||
        e.TimeWindowMinutes       != i.TimeWindowMinutes ||
        e.IsActive              != i.IsActive;

    private static void ApplyChanges(
        Site e,
        SiteSyncItem i,
        SiteOperatingModel model,
        FiscalizationMode fiscalizationMode,
        DateTimeOffset now)
    {
        e.SiteCode              = i.SiteCode;
        e.LegalEntityId         = i.LegalEntityId;
        e.SiteName              = i.SiteName;
        e.OperatingModel        = model;
        e.ConnectivityMode      = i.ConnectivityMode;
        e.CompanyTaxPayerId     = i.CompanyTaxPayerId;
        e.OperatorName          = NormalizeOptional(i.OperatorName);
        e.OperatorTaxPayerId    = NormalizeOptional(i.OperatorTaxPayerId);
        e.SiteUsesPreAuth       = i.SiteUsesPreAuth.GetValueOrDefault();
        e.FiscalizationMode     = fiscalizationMode;
        e.TaxAuthorityEndpoint  = NormalizeOptional(i.TaxAuthorityEndpoint);
        e.RequireCustomerTaxId  = i.RequireCustomerTaxId;
        e.FiscalReceiptRequired = i.FiscalReceiptRequired;
        e.OdooSiteId            = NormalizeOptional(i.OdooSiteId);
        if (i.AmountTolerancePercent.HasValue)
            e.AmountTolerancePercent = i.AmountTolerancePercent.Value;
        if (i.AmountToleranceAbsolute.HasValue)
            e.AmountToleranceAbsolute = i.AmountToleranceAbsolute.Value;
        if (i.TimeWindowMinutes.HasValue)
            e.TimeWindowMinutes = i.TimeWindowMinutes.Value;
        e.IsActive              = i.IsActive;
        e.DeactivatedAt         = i.IsActive ? null : now;
        e.SyncedAt  = now;
        e.UpdatedAt = now;
    }

    private static Site CreateNew(
        SiteSyncItem i,
        SiteOperatingModel model,
        FiscalizationMode fiscalizationMode,
        DateTimeOffset now) => new()
    {
        Id                     = i.Id,
        LegalEntityId          = i.LegalEntityId,
        SiteCode               = i.SiteCode,
        SiteName               = i.SiteName,
        OperatingModel         = model,
        ConnectivityMode       = i.ConnectivityMode,
        CompanyTaxPayerId      = i.CompanyTaxPayerId,
        OperatorName           = NormalizeOptional(i.OperatorName),
        OperatorTaxPayerId     = NormalizeOptional(i.OperatorTaxPayerId),
        SiteUsesPreAuth        = i.SiteUsesPreAuth.GetValueOrDefault(),
        FiscalizationMode      = fiscalizationMode,
        TaxAuthorityEndpoint   = NormalizeOptional(i.TaxAuthorityEndpoint),
        RequireCustomerTaxId   = i.RequireCustomerTaxId,
        FiscalReceiptRequired  = i.FiscalReceiptRequired,
        OdooSiteId             = NormalizeOptional(i.OdooSiteId),
        AmountTolerancePercent = i.AmountTolerancePercent,
        AmountToleranceAbsolute = i.AmountToleranceAbsolute,
        TimeWindowMinutes      = i.TimeWindowMinutes,
        IsActive               = i.IsActive,
        DeactivatedAt          = i.IsActive ? null : now,
        SyncedAt               = now,
        CreatedAt              = now,
        UpdatedAt              = now
    };

    private static bool IsDealerOperated(SiteOperatingModel model) =>
        model is SiteOperatingModel.CODO or SiteOperatingModel.DODO;

    private static bool IsValidConnectivityMode(string connectivityMode) =>
        connectivityMode is "CONNECTED" or "DISCONNECTED";

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
