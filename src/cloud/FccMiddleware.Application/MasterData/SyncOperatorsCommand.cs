using MediatR;

namespace FccMiddleware.Application.MasterData;

public sealed class SyncOperatorsCommand : IRequest<MasterDataSyncResult>
{
    public required List<OperatorSyncItem> Items { get; init; }
}

public sealed class OperatorSyncItem
{
    public Guid Id { get; init; }
    public Guid LegalEntityId { get; init; }
    public string Name { get; init; } = null!;
    public string? TaxPayerId { get; init; }
    public bool IsActive { get; init; }
}
