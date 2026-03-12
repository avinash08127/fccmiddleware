using System.Text.Json;
using FccMiddleware.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.MasterData;

/// <summary>
/// Handles SyncPumpsCommand.
///
/// For each incoming pump:
///   - Resolves SiteId/LegalEntityId via siteCode.
///   - Upserts the pump by id.
///   - Syncs nozzles nested in the payload (upsert by OdooNozzleNumber; soft-delete absent ones).
/// Active pumps absent from the batch are soft-deactivated only for full snapshots.
/// </summary>
public sealed class SyncPumpsHandler : IRequestHandler<SyncPumpsCommand, MasterDataSyncResult>
{
    private readonly IMasterDataSyncDbContext _db;
    private readonly ILogger<SyncPumpsHandler> _logger;

    public SyncPumpsHandler(IMasterDataSyncDbContext db, ILogger<SyncPumpsHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MasterDataSyncResult> Handle(SyncPumpsCommand command, CancellationToken ct)
    {
        var now        = DateTimeOffset.UtcNow;
        var errors     = new List<string>();
        var incomingIds = command.Items.Select(i => i.Id).ToList();

        // Batch-load sites and existing pumps.
        var siteCodes    = command.Items.Select(i => i.SiteCode).Distinct().ToList();
        var siteMap      = await _db.GetSitesByCodesAsync(siteCodes, ct);
        var existingPumps = await _db.GetPumpsByIdsAsync(incomingIds, ct);
        var pumpById     = existingPumps.ToDictionary(p => p.Id);

        // Batch-load all existing nozzles for these pumps.
        var existingNozzles = await _db.GetNozzlesByPumpIdsAsync(incomingIds, ct);
        var nozzlesByPump   = existingNozzles
            .GroupBy(n => n.PumpId)
            .ToDictionary(g => g.Key, g => g.ToList());

        int upserted = 0, unchanged = 0;

        foreach (var item in command.Items)
        {
            if (!siteMap.TryGetValue(item.SiteCode, out var site))
            {
                errors.Add($"Pump {item.Id}: site '{item.SiteCode}' not found.");
                continue;
            }

            Pump pump;
            bool pumpChanged;

            if (pumpById.TryGetValue(item.Id, out var existing))
            {
                pumpChanged = existing.PumpNumber != item.PumpNumber
                           || existing.IsActive   != item.IsActive;

                if (pumpChanged)
                {
                    existing.PumpNumber    = item.PumpNumber;
                    existing.FccPumpNumber = item.PumpNumber; // default 1-to-1 mapping
                    existing.IsActive      = item.IsActive;
                    existing.SyncedAt      = now;
                    existing.UpdatedAt     = now;
                    upserted++;
                }
                else
                {
                    existing.SyncedAt = now;
                    unchanged++;
                }

                pump = existing;
            }
            else
            {
                pump = new Pump
                {
                    Id             = item.Id,
                    SiteId         = site.Id,
                    LegalEntityId  = site.LegalEntityId,
                    PumpNumber     = item.PumpNumber,
                    FccPumpNumber  = item.PumpNumber,
                    IsActive       = item.IsActive,
                    SyncedAt       = now,
                    CreatedAt      = now,
                    UpdatedAt      = now
                };
                _db.AddPump(pump);
                upserted++;
                pumpChanged = true;
            }

            // Sync nozzles for this pump.
            await SyncNozzlesAsync(pump, item.Nozzles, nozzlesByPump, now, errors, ct);
        }

        int deactivated  = 0;

        if (command.IsFullSnapshot)
        {
            var allActiveIds = await _db.GetActivePumpIdsAsync(ct);
            var incomingSet  = incomingIds.ToHashSet();
            var toDeactivate = allActiveIds.Where(id => !incomingSet.Contains(id)).ToList();

            if (toDeactivate.Count > 0)
            {
                var deactivateEntities = await _db.GetPumpsByIdsAsync(toDeactivate, ct);
                foreach (var entity in deactivateEntities)
                {
                    entity.IsActive   = false;
                    entity.SyncedAt   = now;
                    entity.UpdatedAt  = now;
                    deactivated++;
                }
            }
        }

        if (upserted > 0 || deactivated > 0)
        {
            _db.AddOutboxMessage(new OutboxMessage
            {
                EventType = "PumpsSynced",
                Payload   = JsonSerializer.Serialize(new { total = command.Items.Count, upserted, deactivated, syncedAt = now }),
                CreatedAt = now
            });

            _logger.LogInformation(
                "Pump sync: upserted={Upserted}, unchanged={Unchanged}, deactivated={Deactivated}",
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

    private async Task SyncNozzlesAsync(
        Pump pump,
        List<NozzleSyncItem> incomingNozzles,
        Dictionary<Guid, List<Nozzle>> nozzlesByPump,
        DateTimeOffset now,
        List<string> errors,
        CancellationToken ct)
    {
        nozzlesByPump.TryGetValue(pump.Id, out var existing);
        var existingByNumber = (existing ?? []).ToDictionary(n => n.OdooNozzleNumber);
        var incomingNumbers  = incomingNozzles.Select(n => n.NozzleNumber).ToHashSet();

        foreach (var nozzleItem in incomingNozzles)
        {
            var product = await _db.FindProductByCodeAsync(pump.LegalEntityId, nozzleItem.CanonicalProductCode, ct);
            if (product is null)
            {
                errors.Add($"Pump {pump.Id} nozzle {nozzleItem.NozzleNumber}: product '{nozzleItem.CanonicalProductCode}' not found for entity {pump.LegalEntityId}.");
                continue;
            }

            if (existingByNumber.TryGetValue(nozzleItem.NozzleNumber, out var existingNozzle))
            {
                if (existingNozzle.ProductId != product.Id || !existingNozzle.IsActive)
                {
                    existingNozzle.ProductId       = product.Id;
                    existingNozzle.IsActive         = true;
                    existingNozzle.SyncedAt         = now;
                    existingNozzle.UpdatedAt        = now;
                }
                else
                {
                    existingNozzle.SyncedAt = now;
                }
            }
            else
            {
                _db.AddNozzle(new Nozzle
                {
                    Id               = Guid.NewGuid(),
                    PumpId           = pump.Id,
                    SiteId           = pump.SiteId,
                    LegalEntityId    = pump.LegalEntityId,
                    OdooNozzleNumber = nozzleItem.NozzleNumber,
                    FccNozzleNumber  = nozzleItem.NozzleNumber,
                    ProductId        = product.Id,
                    IsActive         = true,
                    SyncedAt         = now,
                    CreatedAt        = now,
                    UpdatedAt        = now
                });
            }
        }

        // Soft-delete nozzles absent from payload.
        foreach (var nozzle in existingByNumber.Values.Where(n => !incomingNumbers.Contains(n.OdooNozzleNumber) && n.IsActive))
        {
            nozzle.IsActive   = false;
            nozzle.SyncedAt   = now;
            nozzle.UpdatedAt  = now;
        }
    }
}
