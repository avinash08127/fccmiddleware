using MediatR;

namespace FccMiddleware.Application.MasterData;

public sealed class SyncLegalEntitiesCommand : IRequest<MasterDataSyncResult>
{
    public bool IsFullSnapshot { get; init; }
    public required List<LegalEntitySyncItem> Items { get; init; }
}

public sealed class LegalEntitySyncItem
{
    public Guid Id { get; init; }
    public string Code { get; init; } = null!;
    public string Name { get; init; } = null!;
    public string CurrencyCode { get; init; } = null!;
    public string? Country { get; init; }
    public string TaxAuthorityCode { get; init; } = null!;
    public string DefaultFiscalizationMode { get; init; } = null!;
    public string? FiscalizationProvider { get; init; }
    public string DefaultTimezone { get; init; } = null!;
    public bool IsActive { get; init; }
}
