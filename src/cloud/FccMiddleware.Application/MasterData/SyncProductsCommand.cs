using MediatR;

namespace FccMiddleware.Application.MasterData;

public sealed class SyncProductsCommand : IRequest<MasterDataSyncResult>
{
    public bool IsFullSnapshot { get; init; }
    public required List<ProductSyncItem> Items { get; init; }
}

public sealed class ProductSyncItem
{
    public Guid Id { get; init; }
    public Guid LegalEntityId { get; init; }
    public string CanonicalCode { get; init; } = null!;
    public string DisplayName { get; init; } = null!;
    public bool IsActive { get; init; }
}
