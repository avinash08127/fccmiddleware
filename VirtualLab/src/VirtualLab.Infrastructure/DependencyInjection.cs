using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Application.Forecourt;
using VirtualLab.Application.Management;
using VirtualLab.Application.Observability;
using VirtualLab.Application.PreAuth;
using VirtualLab.Application.Callbacks;
using VirtualLab.Application.Scenarios;
using VirtualLab.Application.ContractValidation;
using VirtualLab.Infrastructure.Callbacks;
using VirtualLab.Infrastructure.ContractValidation;
using VirtualLab.Infrastructure.Forecourt;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Observability;
using VirtualLab.Infrastructure.Management;
using VirtualLab.Infrastructure.Persistence;
using VirtualLab.Infrastructure.Persistence.Seed;
using VirtualLab.Infrastructure.Diagnostics;
using VirtualLab.Infrastructure.PreAuth;
using VirtualLab.Infrastructure.Scenarios;

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
        services.AddScoped<IVirtualLabManagementService, VirtualLabManagementService>();
        services.AddScoped<IContractValidationService, ContractValidationService>();
        services.AddScoped<IObservabilityService, ObservabilityService>();
        services.AddSingleton<IVirtualLabTelemetry, VirtualLabTelemetry>();
        services.AddScoped<ICallbackCaptureService, CallbackCaptureService>();
        services.AddScoped<IScenarioService, ScenarioService>();
        services.AddScoped<CallbackDeliveryService>();
        services.AddScoped<IForecourtSimulationService, ForecourtSimulationService>();
        services.AddScoped<IPreAuthSimulationService, PreAuthSimulationService>();
        services.AddHostedService<CallbackRetryWorker>();
        services.AddHostedService<PreAuthExpiryWorker>();
        return services;
    }
}
