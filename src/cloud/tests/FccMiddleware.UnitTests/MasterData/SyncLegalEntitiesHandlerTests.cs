using FccMiddleware.Application.MasterData;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FccMiddleware.UnitTests.MasterData;

public sealed class SyncLegalEntitiesHandlerTests
{
    private readonly ILogger<SyncLegalEntitiesHandler> _logger = Substitute.For<ILogger<SyncLegalEntitiesHandler>>();

    [Fact]
    public async Task Handle_UpdatesBusinessCountryAndOdooFields_FromExplicitPayload()
    {
        var db = new FakeMasterDataSyncDbContext();
        var entityId = Guid.NewGuid();
        db.LegalEntities.Add(new LegalEntity
        {
            Id = entityId,
            BusinessCode = "OLD-CODE",
            CountryCode = "OLD-CODE",
            CountryName = "Old Country",
            Name = "Legacy Entity",
            CurrencyCode = "KES",
            TaxAuthorityCode = "KRA",
            DefaultFiscalizationMode = FiscalizationMode.NONE,
            DefaultTimezone = "Africa/Nairobi",
            OdooCompanyId = "ODOO-OLD",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        var sut = new SyncLegalEntitiesHandler(db, _logger);

        var result = await sut.Handle(new SyncLegalEntitiesCommand
        {
            Items =
            [
                new LegalEntitySyncItem
                {
                    Id = entityId,
                    Code = "KE-001",
                    CountryCode = "KE",
                    CountryName = "Kenya",
                    Name = "Kenya Ltd",
                    CurrencyCode = "KES",
                    TaxAuthorityCode = "KRA",
                    DefaultFiscalizationMode = "EXTERNAL_INTEGRATION",
                    FiscalizationProvider = "ETIMS",
                    DefaultTimezone = "Africa/Nairobi",
                    OdooCompanyId = "ODOO-KE-001",
                    IsActive = true
                }
            ]
        }, CancellationToken.None);

        result.UpsertedCount.Should().Be(1);
        result.ErrorCount.Should().Be(0);

        var updated = db.LegalEntities.Single();
        updated.BusinessCode.Should().Be("KE-001");
        updated.CountryCode.Should().Be("KE");
        updated.CountryName.Should().Be("Kenya");
        updated.OdooCompanyId.Should().Be("ODOO-KE-001");
        updated.DefaultFiscalizationMode.Should().Be(FiscalizationMode.EXTERNAL_INTEGRATION);
        updated.FiscalizationProvider.Should().Be("ETIMS");
    }

    [Fact]
    public async Task Handle_RejectsMissingRequirementFields()
    {
        var db = new FakeMasterDataSyncDbContext();
        var sut = new SyncLegalEntitiesHandler(db, _logger);

        var result = await sut.Handle(new SyncLegalEntitiesCommand
        {
            Items =
            [
                new LegalEntitySyncItem
                {
                    Id = Guid.NewGuid(),
                    Code = "GH-001",
                    CountryCode = "GH",
                    CountryName = "",
                    Name = "Ghana Ltd",
                    CurrencyCode = "GHS",
                    TaxAuthorityCode = "GRA",
                    DefaultFiscalizationMode = "NONE",
                    DefaultTimezone = "Africa/Accra",
                    OdooCompanyId = "",
                    IsActive = true
                }
            ]
        }, CancellationToken.None);

        result.UpsertedCount.Should().Be(0);
        result.ErrorCount.Should().Be(1);
        result.Errors.Should().ContainSingle(error => error.Contains("countryName is required"));
        db.LegalEntities.Should().BeEmpty();
    }
}
