using FluentAssertions;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;
using FccMiddleware.Infrastructure.Adapters;

namespace FccMiddleware.Adapter.Doms.Tests;

public class FccAdapterFactoryTests
{
    private static FccAdapterFactory CreateFactory(
        params (FccVendor Vendor, Func<SiteFccConfig, IFccAdapter> Creator)[] entries)
    {
        var registry = entries.ToDictionary(e => e.Vendor, e => e.Creator);
        return new FccAdapterFactory(registry);
    }

    private static IFccAdapter DomsCreator(SiteFccConfig cfg) =>
        new DomsCloudAdapter(
            new HttpClient { BaseAddress = new Uri("http://localhost:8080/api/v1/") },
            cfg);

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_RegisteredVendor_ReturnsCorrectAdapter()
    {
        var factory = CreateFactory((FccVendor.DOMS, DomsCreator));
        var config = TestHelpers.DefaultConfig();

        var adapter = factory.Resolve(FccVendor.DOMS, config);

        adapter.Should().NotBeNull();
        adapter.Should().BeOfType<DomsCloudAdapter>();
    }

    [Fact]
    public void Resolve_ReturnedAdapter_ReportsCorrectVendorMetadata()
    {
        var factory = CreateFactory((FccVendor.DOMS, DomsCreator));
        var config = TestHelpers.DefaultConfig();

        var adapter = factory.Resolve(FccVendor.DOMS, config);
        var metadata = adapter.GetAdapterMetadata();

        metadata.Vendor.Should().Be(FccVendor.DOMS);
    }

    // -------------------------------------------------------------------------
    // Unknown vendor → ADAPTER_NOT_REGISTERED
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_UnregisteredVendor_ThrowsAdapterNotRegisteredException()
    {
        var factory = CreateFactory((FccVendor.DOMS, DomsCreator));
        var config = TestHelpers.DefaultConfig();

        var act = () => factory.Resolve(FccVendor.RADIX, config);

        act.Should().Throw<AdapterNotRegisteredException>()
           .WithMessage($"*{AdapterNotRegisteredException.ErrorCode}*")
           .And.Vendor.Should().Be(FccVendor.RADIX);
    }

    [Theory]
    [InlineData(FccVendor.RADIX)]
    [InlineData(FccVendor.ADVATEC)]
    [InlineData(FccVendor.PETRONITE)]
    public void Resolve_EmptyRegistry_ThrowsAdapterNotRegisteredExceptionForAnyVendor(FccVendor vendor)
    {
        var factory = new FccAdapterFactory(
            new Dictionary<FccVendor, Func<SiteFccConfig, IFccAdapter>>());
        var config = TestHelpers.DefaultConfig();

        var act = () => factory.Resolve(vendor, config);

        act.Should().Throw<AdapterNotRegisteredException>()
           .And.Vendor.Should().Be(vendor);
    }

    // -------------------------------------------------------------------------
    // Multiple registered vendors
    // -------------------------------------------------------------------------

    [Fact]
    public void Resolve_AdditionalRegisteredVendor_UsesBoundCreator()
    {
        var factory = CreateFactory(
            (FccVendor.DOMS, DomsCreator),
            (FccVendor.RADIX, DomsCreator));
        var config = TestHelpers.DefaultConfig() with { FccVendor = FccVendor.RADIX };

        var adapter = factory.Resolve(FccVendor.RADIX, config);

        adapter.Should().BeOfType<DomsCloudAdapter>();
    }

    // -------------------------------------------------------------------------
    // Create helper
    // -------------------------------------------------------------------------

    [Fact]
    public void Create_ConfigureAction_BuildsFactoryCorrectly()
    {
        var factory = FccAdapterFactory.Create(registry =>
        {
            registry[FccVendor.DOMS] = DomsCreator;
        });

        var config = TestHelpers.DefaultConfig();
        var adapter = factory.Resolve(FccVendor.DOMS, config);

        adapter.Should().BeOfType<DomsCloudAdapter>();
    }
}
