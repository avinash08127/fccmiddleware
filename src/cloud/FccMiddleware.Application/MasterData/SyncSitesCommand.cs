using MediatR;

namespace FccMiddleware.Application.MasterData;

public sealed class SyncSitesCommand : IRequest<MasterDataSyncResult>
{
    public bool IsFullSnapshot { get; init; }
    public required List<SiteSyncItem> Items { get; init; }
}

public sealed class SiteSyncItem
{
    public Guid Id { get; init; }
    public string SiteCode { get; init; } = null!;
    public Guid LegalEntityId { get; init; }
    public string SiteName { get; init; } = null!;
    public string OperatingModel { get; init; } = null!;
    public string ConnectivityMode { get; init; } = null!;
    public string CompanyTaxPayerId { get; init; } = null!;
    public string? OperatorName { get; init; }
    public string? OperatorTaxPayerId { get; init; }
    public bool? SiteUsesPreAuth { get; init; }
    public string FiscalizationMode { get; init; } = null!;
    public string? TaxAuthorityEndpoint { get; init; }
    public bool RequireCustomerTaxId { get; init; }
    public bool FiscalReceiptRequired { get; init; }
    public string? OdooSiteId { get; init; }
    public bool IsActive { get; init; }
}
