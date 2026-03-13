using FccMiddleware.Adapter.Advatec;
using FccMiddleware.Adapter.Doms;
using FccMiddleware.Adapter.Petronite;
using FccMiddleware.Adapter.Radix;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models.Adapter;
using FccMiddleware.Infrastructure.Adapters;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FccMiddleware.Infrastructure.Tests.Adapters;

public sealed class CloudFccAdapterFactoryRegistrationTests
{
    private static readonly SiteFccConfig BaseConfig = new()
    {
        SiteCode = "SITE-001",
        FccVendor = FccVendor.DOMS,
        ConnectionProtocol = ConnectionProtocol.REST,
        HostAddress = "127.0.0.1",
        Port = 8080,
        ApiKey = "test-key",
        IngestionMethod = IngestionMethod.PULL,
        CurrencyCode = "ZAR",
        Timezone = "Africa/Johannesburg",
    };

    [Fact]
    public void SupportedVendors_MatchesExpectedMatrix()
    {
        CloudFccAdapterFactoryRegistration.SupportedVendors
            .Should()
            .BeEquivalentTo([FccVendor.DOMS, FccVendor.RADIX, FccVendor.PETRONITE, FccVendor.ADVATEC]);
    }

    [Theory]
    [InlineData(FccVendor.DOMS, typeof(DomsCloudAdapter))]
    [InlineData(FccVendor.RADIX, typeof(RadixCloudAdapter))]
    [InlineData(FccVendor.PETRONITE, typeof(PetroniteCloudAdapter))]
    [InlineData(FccVendor.ADVATEC, typeof(AdvatecCloudAdapter))]
    public void CreateFactory_ResolvesAllSupportedVendors(FccVendor vendor, Type expectedType)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(_ => Substitute.For<ILogger>());

        var factory = CloudFccAdapterFactoryRegistration.CreateFactory(httpClientFactory, loggerFactory);
        var config = BaseConfig with { FccVendor = vendor };

        var adapter = factory.Resolve(vendor, config);

        adapter.Should().BeOfType(expectedType);
    }
}
