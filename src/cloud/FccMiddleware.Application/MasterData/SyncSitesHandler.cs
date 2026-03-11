using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.MasterData;

/// <summary>
/// Handles SyncSitesCommand — upserts sites and soft-deletes absent ones.
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

            if (byId.TryGetValue(item.Id, out var entity))
            {
                if (HasChanges(entity, item, model))
                {
                    ApplyChanges(entity, item, model, now);
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
                _db.AddSite(CreateNew(item, model, now));
                upserted++;
            }
        }

        // Soft-delete active sites absent from this batch.
        var allActiveIds = await _db.GetActiveSiteIdsAsync(ct);
        var incomingSet  = incomingIds.ToHashSet();
        var toDeactivate = allActiveIds.Where(id => !incomingSet.Contains(id)).ToList();
        int deactivated  = 0;

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

    private static bool HasChanges(Site e, SiteSyncItem i, SiteOperatingModel model) =>
        e.SiteCode      != i.SiteCode      ||
        e.LegalEntityId != i.LegalEntityId ||
        e.SiteName      != i.SiteName      ||
        e.OperatingModel != model           ||
        e.IsActive      != i.IsActive;

    private static void ApplyChanges(Site e, SiteSyncItem i, SiteOperatingModel model, DateTimeOffset now)
    {
        e.SiteCode       = i.SiteCode;
        e.LegalEntityId  = i.LegalEntityId;
        e.SiteName       = i.SiteName;
        e.OperatingModel = model;
        e.IsActive       = i.IsActive;
        if (!i.IsActive) e.DeactivatedAt = now;
        e.SyncedAt  = now;
        e.UpdatedAt = now;
    }

    private static Site CreateNew(SiteSyncItem i, SiteOperatingModel model, DateTimeOffset now) => new()
    {
        Id              = i.Id,
        LegalEntityId   = i.LegalEntityId,
        SiteCode        = i.SiteCode,
        SiteName        = i.SiteName,
        OperatingModel  = model,
        ConnectivityMode = "CONNECTED",
        CompanyTaxPayerId = string.Empty,
        IsActive        = i.IsActive,
        DeactivatedAt   = i.IsActive ? null : now,
        SyncedAt        = now,
        CreatedAt       = now,
        UpdatedAt       = now
    };
}
