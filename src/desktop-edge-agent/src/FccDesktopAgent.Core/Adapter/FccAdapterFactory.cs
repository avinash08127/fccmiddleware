using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms;
using FccDesktopAgent.Core.Adapter.Petronite;
using FccDesktopAgent.Core.Adapter.Radix;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter;

/// <summary>
/// Concrete factory that creates FCC adapter instances by vendor type.
/// For DOMS, selects TCP/JPL or REST based on <see cref="FccConnectionConfig.ConnectionProtocol"/>.
/// Adapters are lightweight — a new instance is created per use.
/// </summary>
public sealed class FccAdapterFactory : IFccAdapterFactory
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILoggerFactory _loggerFactory;

    public FccAdapterFactory(IHttpClientFactory httpFactory, ILoggerFactory loggerFactory)
    {
        _httpFactory = httpFactory;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc/>
    public IFccAdapter Create(FccVendor vendor, FccConnectionConfig config) => vendor switch
    {
        FccVendor.Doms => config.ConnectionProtocol?.Equals("TCP", StringComparison.OrdinalIgnoreCase) == true
            ? CreateDomsJplAdapter(config)
            : new DomsAdapter(
                _httpFactory,
                config,
                _loggerFactory.CreateLogger<DomsAdapter>()),
        FccVendor.Radix => new RadixAdapter(
            _httpFactory,
            config,
            _loggerFactory.CreateLogger<RadixAdapter>()),
        FccVendor.Petronite => CreatePetroniteAdapter(config),
        FccVendor.Advatec => throw new NotImplementedException(
            "Advatec adapter is not yet implemented"),
        _ => throw new ArgumentException($"Unknown FCC vendor: {vendor}", nameof(vendor))
    };

    private PetroniteAdapter CreatePetroniteAdapter(FccConnectionConfig config)
    {
        var oauthClient = new PetroniteOAuthClient(
            _httpFactory,
            config,
            _loggerFactory.CreateLogger<PetroniteOAuthClient>());

        var nozzleResolver = new PetroniteNozzleResolver(
            _httpFactory,
            config,
            oauthClient,
            _loggerFactory.CreateLogger<PetroniteNozzleResolver>());

        return new PetroniteAdapter(
            _httpFactory,
            config,
            oauthClient,
            nozzleResolver,
            _loggerFactory.CreateLogger<PetroniteAdapter>());
    }

    private DomsJplAdapter CreateDomsJplAdapter(FccConnectionConfig config)
    {
        return new DomsJplAdapter(
            config: config,
            siteCode: config.SiteCode,
            legalEntityId: config.SiteCode, // Resolved from site config at runtime
            currencyCode: "ZAR", // Default; overridden by site config at runtime
            timezone: "Africa/Johannesburg", // Default; overridden by site config at runtime
            pumpNumberOffset: 0, // Default; overridden by site config at runtime
            productCodeMapping: null,
            logger: _loggerFactory.CreateLogger<DomsJplAdapter>());
    }
}
