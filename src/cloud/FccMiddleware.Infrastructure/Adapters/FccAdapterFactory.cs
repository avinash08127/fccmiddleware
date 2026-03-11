using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Exceptions;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Infrastructure.Adapters;

/// <summary>
/// Resolves IFccAdapter implementations by FccVendor using a registry of creator delegates.
/// One delegate per FccVendor is permitted. Resolution fails fast with
/// AdapterNotRegisteredException for any vendor without a registered delegate.
///
/// Register in DI (e.g., Program.cs):
/// <code>
/// services.AddSingleton&lt;IFccAdapterFactory&gt;(sp =>
/// {
///     var hcf = sp.GetRequiredService&lt;IHttpClientFactory&gt;();
///     return FccAdapterFactory.Create(registry =>
///     {
///         registry[FccVendor.DOMS] = cfg =>
///         {
///             var client = hcf.CreateClient();
///             client.BaseAddress = new Uri($"http://{cfg.HostAddress}:{cfg.Port}/api/v1/");
///             client.DefaultRequestHeaders.Add("X-API-Key", cfg.ApiKey);
///             return new DomsCloudAdapter(client, cfg);
///         };
///     });
/// });
/// </code>
/// </summary>
public sealed class FccAdapterFactory : IFccAdapterFactory
{
    private readonly IReadOnlyDictionary<FccVendor, Func<SiteFccConfig, IFccAdapter>> _registry;

    public FccAdapterFactory(
        IReadOnlyDictionary<FccVendor, Func<SiteFccConfig, IFccAdapter>> registry)
    {
        _registry = registry;
    }

    /// <summary>Convenience factory method for DI wiring.</summary>
    public static FccAdapterFactory Create(
        Action<Dictionary<FccVendor, Func<SiteFccConfig, IFccAdapter>>> configure)
    {
        var registry = new Dictionary<FccVendor, Func<SiteFccConfig, IFccAdapter>>();
        configure(registry);
        return new FccAdapterFactory(registry);
    }

    /// <inheritdoc />
    public IFccAdapter Resolve(FccVendor vendor, SiteFccConfig config)
    {
        if (!_registry.TryGetValue(vendor, out var create))
            throw new AdapterNotRegisteredException(vendor);

        return create(config);
    }
}
