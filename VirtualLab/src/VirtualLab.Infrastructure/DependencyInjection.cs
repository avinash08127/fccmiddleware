using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Application.Forecourt;
using VirtualLab.Application.PreAuth;
using VirtualLab.Infrastructure.Forecourt;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Persistence;
using VirtualLab.Infrastructure.Persistence.Seed;
using VirtualLab.Infrastructure.Diagnostics;
using VirtualLab.Infrastructure.PreAuth;

namespace VirtualLab.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddVirtualLabInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VirtualLabPersistenceOptions>(configuration.GetSection(VirtualLabPersistenceOptions.SectionName));
        services.Configure<VirtualLabSeedOptions>(configuration.GetSection(VirtualLabSeedOptions.SectionName));
        services.Configure<CallbackDeliveryOptions>(configuration.GetSection(CallbackDeliveryOptions.SectionName));

        services.AddDbContext<VirtualLabDbContext>((serviceProvider, options) =>
        {
            VirtualLabPersistenceOptions persistenceOptions = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<VirtualLabPersistenceOptions>>()
                .Value;

            if (!string.Equals(persistenceOptions.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported VirtualLab provider '{persistenceOptions.Provider}'.");
            }

            options.UseSqlite(
                persistenceOptions.ConnectionString,
                sqliteOptions => sqliteOptions.MigrationsAssembly(typeof(VirtualLabDbContext).Assembly.FullName));
            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        services.AddSingleton<ApiTimingStore>();
        services.AddHttpClient("VirtualLab.CallbackDispatch", (serviceProvider, client) =>
        {
            CallbackDeliveryOptions callbackOptions = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<CallbackDeliveryOptions>>()
                .Value;

            client.Timeout = TimeSpan.FromSeconds(Math.Max(callbackOptions.RequestTimeoutSeconds, 1));
        });
        services.AddScoped<IVirtualLabSeedService, VirtualLabSeedService>();
        services.AddScoped<IFccProfileService, FccProfileService>();
        services.AddScoped<CallbackDeliveryService>();
        services.AddScoped<IForecourtSimulationService, ForecourtSimulationService>();
        services.AddScoped<IPreAuthSimulationService, PreAuthSimulationService>();
        services.AddHostedService<CallbackRetryWorker>();
        services.AddHostedService<PreAuthExpiryWorker>();
        return services;
    }
}
