using FccMiddleware.Adapter.Advatec;
using FccMiddleware.Adapter.Doms;
using FccMiddleware.Adapter.Petronite;
using FccMiddleware.Adapter.Radix;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;

namespace FccMiddleware.Infrastructure.Adapters;

/// <summary>
/// Shared FCC adapter registration for cloud runtimes.
/// API and worker must use the same factory wiring to keep vendor support in sync.
/// </summary>
public static class CloudFccAdapterFactoryRegistration
{
    public static IReadOnlySet<FccVendor> SupportedVendors { get; } = new HashSet<FccVendor>
    {
        FccVendor.DOMS,
        FccVendor.RADIX,
        FccVendor.PETRONITE,
        FccVendor.ADVATEC,
    };

    public static bool IsSupported(FccVendor vendor) => SupportedVendors.Contains(vendor);

    public static IServiceCollection AddCloudFccAdapterFactory(this IServiceCollection services)
    {
        services.AddHttpClient();
        services.AddSingleton<IFccAdapterFactory>(sp =>
            CreateFactory(sp.GetRequiredService<IHttpClientFactory>()));

        return services;
    }

    public static IFccAdapterFactory CreateFactory(IHttpClientFactory httpClientFactory) =>
        FccAdapterFactory.Create(registry =>
        {
            registry[FccVendor.DOMS] = cfg =>
            {
                var client = httpClientFactory.CreateClient();
                if (!string.IsNullOrEmpty(cfg.HostAddress))
                {
                    client.BaseAddress = new Uri($"http://{cfg.HostAddress}:{cfg.Port}/api/v1/");
                    client.DefaultRequestHeaders.Add("X-API-Key", cfg.ApiKey);
                }

                return new DomsCloudAdapter(client, cfg);
            };

            registry[FccVendor.RADIX] = cfg =>
            {
                var client = httpClientFactory.CreateClient();
                if (!string.IsNullOrEmpty(cfg.HostAddress))
                {
                    client.BaseAddress = new Uri($"http://{cfg.HostAddress}:{cfg.Port}/");
                }

                return new RadixCloudAdapter(client, cfg);
            };

            registry[FccVendor.PETRONITE] = cfg => new PetroniteCloudAdapter(cfg);

            registry[FccVendor.ADVATEC] = cfg => new AdvatecCloudAdapter(cfg);
        });
}
