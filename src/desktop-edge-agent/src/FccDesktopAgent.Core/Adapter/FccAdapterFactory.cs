using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Adapter.Doms;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter;

/// <summary>
/// Concrete factory that creates FCC adapter instances by vendor type.
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
        FccVendor.Doms => new DomsAdapter(
            _httpFactory,
            config,
            _loggerFactory.CreateLogger<DomsAdapter>()),
        FccVendor.Radix => throw new NotImplementedException(
            "Radix adapter is not yet implemented"),
        _ => throw new ArgumentException($"Unknown FCC vendor: {vendor}", nameof(vendor))
    };
}
