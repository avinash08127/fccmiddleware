using MediatR;

namespace FccMiddleware.Application.MasterData;

public sealed class SyncSitesCommand : IRequest<MasterDataSyncResult>
{
    public required List<SiteSyncItem> Items { get; init; }
}

public sealed class SiteSyncItem
{
    public Guid Id { get; init; }
    public string SiteCode { get; init; } = null!;
    public Guid LegalEntityId { get; init; }
    public string SiteName { get; init; } = null!;
    public string OperatingModel { get; init; } = null!;
    public bool IsActive { get; init; }
}
