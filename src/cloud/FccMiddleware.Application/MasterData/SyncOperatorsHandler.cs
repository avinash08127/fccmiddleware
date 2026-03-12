using System.Text.Json;
using FccMiddleware.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.MasterData;

/// <summary>
/// Handles SyncOperatorsCommand — upserts operators per legal entity and only soft-deletes absent ones
/// for full snapshots.
/// </summary>
public sealed class SyncOperatorsHandler : IRequestHandler<SyncOperatorsCommand, MasterDataSyncResult>
{
    private readonly IMasterDataSyncDbContext _db;
    private readonly ILogger<SyncOperatorsHandler> _logger;

    public SyncOperatorsHandler(IMasterDataSyncDbContext db, ILogger<SyncOperatorsHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MasterDataSyncResult> Handle(SyncOperatorsCommand command, CancellationToken ct)
    {
        var now        = DateTimeOffset.UtcNow;
        var incomingIds = command.Items.Select(i => i.Id).ToList();

        var existing = await _db.GetOperatorsByIdsAsync(incomingIds, ct);
        var byId     = existing.ToDictionary(e => e.Id);

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
                _db.AddOperator(CreateNew(item, now));
                upserted++;
            }
        }

        int deactivated    = 0;

        if (command.IsFullSnapshot)
        {
            var legalEntityIds = command.Items.Select(i => i.LegalEntityId).Distinct().ToList();
            var incomingSet    = incomingIds.ToHashSet();

            foreach (var leiId in legalEntityIds)
            {
                var activeIds    = await _db.GetActiveOperatorIdsAsync(leiId, ct);
                var toDeactivate = activeIds.Where(id => !incomingSet.Contains(id)).ToList();

                if (toDeactivate.Count > 0)
                {
                    var deactivateEntities = await _db.GetOperatorsByIdsAsync(toDeactivate, ct);
                    foreach (var entity in deactivateEntities)
                    {
                        entity.IsActive  = false;
                        entity.UpdatedAt = now;
                        deactivated++;
                    }
                }
            }
        }

        if (upserted > 0 || deactivated > 0)
        {
            _db.AddOutboxMessage(new OutboxMessage
            {
                EventType = "OperatorsSynced",
                Payload   = JsonSerializer.Serialize(new { total = command.Items.Count, upserted, deactivated, syncedAt = now }),
                CreatedAt = now
            });

            _logger.LogInformation(
                "Operator sync: upserted={Upserted}, unchanged={Unchanged}, deactivated={Deactivated}",
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

    private static bool HasChanges(Operator e, OperatorSyncItem i) =>
        e.OperatorName  != i.Name        ||
        e.TaxPayerId    != i.TaxPayerId  ||
        e.LegalEntityId != i.LegalEntityId ||
        e.IsActive      != i.IsActive;

    private static void ApplyChanges(Operator e, OperatorSyncItem i, DateTimeOffset now)
    {
        e.OperatorName  = i.Name;
        e.TaxPayerId    = i.TaxPayerId;
        e.LegalEntityId = i.LegalEntityId;
        e.IsActive      = i.IsActive;
        e.SyncedAt      = now;
        e.UpdatedAt     = now;
    }

    private static Operator CreateNew(OperatorSyncItem i, DateTimeOffset now) => new()
    {
        Id              = i.Id,
        LegalEntityId   = i.LegalEntityId,
        OperatorName    = i.Name,
        OperatorCode    = i.Id.ToString("N")[..16], // stable derived code from ID
        TaxPayerId      = i.TaxPayerId,
        IsActive        = i.IsActive,
        SyncedAt        = now,
        CreatedAt       = now,
        UpdatedAt       = now
    };
}
