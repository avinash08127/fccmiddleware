using FccMiddleware.Application.MasterData;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FccMiddleware.UnitTests.MasterData;

public sealed class SyncSitesHandlerTests
{
    private readonly ILogger<SyncSitesHandler> _logger = Substitute.For<ILogger<SyncSitesHandler>>();

    [Fact]
    public async Task Handle_PersistsSiteUsesPreAuth_FromSyncPayload()
    {
        var db = new FakeMasterDataSyncDbContext();
        var siteId = Guid.NewGuid();
        db.Sites.Add(new Site
        {
            Id = siteId,
            LegalEntityId = Guid.NewGuid(),
            SiteCode = "SITE-001",
            SiteName = "Existing Site",
            OperatingModel = SiteOperatingModel.COCO,
            SiteUsesPreAuth = false,
            ConnectivityMode = "CONNECTED",
            CompanyTaxPayerId = "TIN-001",
            FiscalizationMode = FiscalizationMode.NONE,
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        var sut = new SyncSitesHandler(db, _logger);

        var result = await sut.Handle(new SyncSitesCommand
        {
            Items =
            [
                new SiteSyncItem
                {
                    Id = siteId,
                    LegalEntityId = db.Sites[0].LegalEntityId,
                    SiteCode = "SITE-001",
                    SiteName = "Existing Site",
                    OperatingModel = "COCO",
                    ConnectivityMode = "CONNECTED",
                    CompanyTaxPayerId = "TIN-001",
                    SiteUsesPreAuth = true,
                    FiscalizationMode = "NONE",
                    RequireCustomerTaxId = false,
                    FiscalReceiptRequired = false,
                    IsActive = true
                }
            ]
        }, CancellationToken.None);

        result.UpsertedCount.Should().Be(1);
        result.ErrorCount.Should().Be(0);
        db.Sites.Single().SiteUsesPreAuth.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_RejectsPayloadWithoutSiteUsesPreAuth()
    {
        var db = new FakeMasterDataSyncDbContext();
        var sut = new SyncSitesHandler(db, _logger);

        var result = await sut.Handle(new SyncSitesCommand
        {
            Items =
            [
                new SiteSyncItem
                {
                    Id = Guid.NewGuid(),
                    LegalEntityId = Guid.NewGuid(),
                    SiteCode = "SITE-001",
                    SiteName = "Missing Flag Site",
                    OperatingModel = "COCO",
                    ConnectivityMode = "CONNECTED",
                    CompanyTaxPayerId = "TIN-001",
                    FiscalizationMode = "NONE",
                    RequireCustomerTaxId = false,
                    FiscalReceiptRequired = false,
                    IsActive = true
                }
            ]
        }, CancellationToken.None);

        result.UpsertedCount.Should().Be(0);
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().ContainSingle(error => error.Contains("siteUsesPreAuth is required"));
        db.Sites.Should().BeEmpty();
    }
}
