using System.Text.Json;
using FccMiddleware.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.MasterData;

/// <summary>
/// Handles SyncProductsCommand — upserts products per legal entity and only soft-deletes absent ones
/// for full snapshots.
/// </summary>
public sealed class SyncProductsHandler : IRequestHandler<SyncProductsCommand, MasterDataSyncResult>
{
    private readonly IMasterDataSyncDbContext _db;
    private readonly ILogger<SyncProductsHandler> _logger;

    public SyncProductsHandler(IMasterDataSyncDbContext db, ILogger<SyncProductsHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MasterDataSyncResult> Handle(SyncProductsCommand command, CancellationToken ct)
    {
        var now        = DateTimeOffset.UtcNow;
        var incomingIds = command.Items.Select(i => i.Id).ToList();

        var existing = await _db.GetProductsByIdsAsync(incomingIds, ct);
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
                _db.AddProduct(CreateNew(item, now));
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
                var activeIds    = await _db.GetActiveProductIdsAsync(leiId, ct);
                var toDeactivate = activeIds.Where(id => !incomingSet.Contains(id)).ToList();

                if (toDeactivate.Count > 0)
                {
                    var deactivateEntities = await _db.GetProductsByIdsAsync(toDeactivate, ct);
                    foreach (var entity in deactivateEntities)
                    {
                        entity.IsActive   = false;
                        entity.UpdatedAt  = now;
                        deactivated++;
                    }
                }
            }
        }

        if (upserted > 0 || deactivated > 0)
        {
            _db.AddOutboxMessage(new OutboxMessage
            {
                EventType = "ProductsSynced",
                Payload   = JsonSerializer.Serialize(new { total = command.Items.Count, upserted, deactivated, syncedAt = now }),
                CreatedAt = now
            });

            _logger.LogInformation(
                "Product sync: upserted={Upserted}, unchanged={Unchanged}, deactivated={Deactivated}",
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

    private static bool HasChanges(Product e, ProductSyncItem i) =>
        e.ProductCode != i.CanonicalCode ||
        e.ProductName != i.DisplayName   ||
        e.IsActive    != i.IsActive;

    private static void ApplyChanges(Product e, ProductSyncItem i, DateTimeOffset now)
    {
        e.ProductCode = i.CanonicalCode;
        e.ProductName = i.DisplayName;
        e.IsActive    = i.IsActive;
        e.SyncedAt    = now;
        e.UpdatedAt   = now;
    }

    private static Product CreateNew(ProductSyncItem i, DateTimeOffset now) => new()
    {
        Id            = i.Id,
        LegalEntityId = i.LegalEntityId,
        ProductCode   = i.CanonicalCode,
        ProductName   = i.DisplayName,
        UnitOfMeasure = "LITRE",
        IsActive      = i.IsActive,
        SyncedAt      = now,
        CreatedAt     = now,
        UpdatedAt     = now
    };
}
