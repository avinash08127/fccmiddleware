using System.Text.Json;
using FccMiddleware.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.MasterData;

/// <summary>
/// Handles SyncLegalEntitiesCommand.
///
/// For each incoming record:
///   - If found in DB: compare fields; update if changed (upsertedCount) or skip (unchangedCount).
///   - If not found: insert new record (upsertedCount).
/// Active records absent from the payload are soft-deactivated only for full snapshots.
/// Publishes a MasterDataSynced outbox event on any change.
/// </summary>
public sealed class SyncLegalEntitiesHandler : IRequestHandler<SyncLegalEntitiesCommand, MasterDataSyncResult>
{
    private readonly IMasterDataSyncDbContext _db;
    private readonly ILogger<SyncLegalEntitiesHandler> _logger;

    public SyncLegalEntitiesHandler(IMasterDataSyncDbContext db, ILogger<SyncLegalEntitiesHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MasterDataSyncResult> Handle(SyncLegalEntitiesCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var incomingIds = command.Items.Select(i => i.Id).ToList();

        var existing = await _db.GetLegalEntitiesByIdsAsync(incomingIds, ct);
        var byId = existing.ToDictionary(e => e.Id);

        int upserted = 0, unchanged = 0;

        foreach (var item in command.Items)
        {
            if (byId.TryGetValue(item.Id, out var entity))
            {
                if (HasChanges(entity, item))
                {
                    ApplyChanges(entity, item, now);
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
                _db.AddLegalEntity(CreateNew(item, now));
                upserted++;
            }
        }

        var deactivated = 0;

        if (command.IsFullSnapshot)
        {
            var allActiveIds = await _db.GetActiveLegalEntityIdsAsync(ct);
            var incomingSet = incomingIds.ToHashSet();
            var toDeactivate = allActiveIds.Where(id => !incomingSet.Contains(id)).ToList();

            if (toDeactivate.Count > 0)
            {
                var deactivateEntities = await _db.GetLegalEntitiesByIdsAsync(toDeactivate, ct);
                foreach (var entity in deactivateEntities)
                {
                    entity.IsActive = false;
                    entity.DeactivatedAt = now;
                    entity.UpdatedAt = now;
                    deactivated++;
                }
            }
        }

        if (upserted > 0 || deactivated > 0)
        {
            _db.AddOutboxMessage(BuildOutboxMessage("LegalEntitiesSynced", incomingIds.Count, upserted, deactivated, now));
            _logger.LogInformation(
                "Legal entity sync: upserted={Upserted}, unchanged={Unchanged}, deactivated={Deactivated}",
                upserted, unchanged, deactivated);
        }

        await _db.SaveChangesAsync(ct);

        return new MasterDataSyncResult
        {
            UpsertedCount    = upserted,
            UnchangedCount   = unchanged,
            DeactivatedCount = deactivated
        };
    }

    private static bool HasChanges(LegalEntity e, LegalEntitySyncItem i) =>
        e.CountryCode  != i.Code     ||
        e.Name         != i.Name     ||
        e.CurrencyCode != i.CurrencyCode ||
        e.IsActive     != i.IsActive;

    private static void ApplyChanges(LegalEntity e, LegalEntitySyncItem i, DateTimeOffset now)
    {
        e.CountryCode  = i.Code;
        e.Name         = i.Name;
        e.CurrencyCode = i.CurrencyCode;
        e.IsActive     = i.IsActive;
        if (!i.IsActive) e.DeactivatedAt = now;
        e.SyncedAt  = now;
        e.UpdatedAt = now;
    }

    private static LegalEntity CreateNew(LegalEntitySyncItem i, DateTimeOffset now) => new()
    {
        Id                    = i.Id,
        CountryCode           = i.Code,
        Name                  = i.Name,
        CurrencyCode          = i.CurrencyCode,
        TaxAuthorityCode      = string.Empty,
        FiscalizationRequired = false,
        DefaultTimezone       = "UTC",
        IsActive              = i.IsActive,
        DeactivatedAt         = i.IsActive ? null : now,
        SyncedAt              = now,
        CreatedAt             = now,
        UpdatedAt             = now
    };

    private static OutboxMessage BuildOutboxMessage(string eventType, int total, int upserted, int deactivated, DateTimeOffset now) =>
        new()
        {
            EventType = eventType,
            Payload   = JsonSerializer.Serialize(new { total, upserted, deactivated, syncedAt = now }),
            CreatedAt = now
        };
}
