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
using VirtualLab.Infrastructure.DomsJpl;
using VirtualLab.Infrastructure.Forecourt;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Observability;
using VirtualLab.Infrastructure.Management;
using VirtualLab.Infrastructure.Persistence;
using VirtualLab.Infrastructure.Persistence.Seed;
using VirtualLab.Infrastructure.Diagnostics;
using VirtualLab.Infrastructure.PetroniteSimulator;
using VirtualLab.Infrastructure.PreAuth;
using VirtualLab.Infrastructure.RadixSimulator;
using VirtualLab.Infrastructure.Scenarios;

namespace VirtualLab.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddVirtualLabInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<VirtualLabPersistenceOptions>(configuration.GetSection(VirtualLabPersistenceOptions.SectionName));
        services.Configure<VirtualLabSeedOptions>(configuration.GetSection(VirtualLabSeedOptions.SectionName));
        services.Configure<CallbackDeliveryOptions>(configuration.GetSection(CallbackDeliveryOptions.SectionName));
        services.Configure<VirtualLabCorsOptions>(configuration.GetSection(VirtualLabCorsOptions.SectionName));

        services.AddDbContext<VirtualLabDbContext>((serviceProvider, options) =>
        {
            VirtualLabPersistenceOptions persistenceOptions = serviceProvider
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<VirtualLabPersistenceOptions>>()
                .Value;

            switch (persistenceOptions.Provider.Trim())
            {
                case "Sqlite":
                case "sqlite":
                    options.UseSqlite(
                        persistenceOptions.ConnectionString,
                        sqliteOptions => sqliteOptions.MigrationsAssembly(typeof(VirtualLabDbContext).Assembly.FullName));
                    break;
                case "SqlServer":
                case "sqlserver":
                case "Sql Server":
                case "sql server":
                    options.UseSqlServer(
                        persistenceOptions.ConnectionString,
                        sqlServerOptions =>
                        {
                            sqlServerOptions.MigrationsAssembly(typeof(VirtualLabDbContext).Assembly.FullName);
                            sqlServerOptions.EnableRetryOnFailure();
                        });
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported VirtualLab provider '{persistenceOptions.Provider}'. Supported providers are Sqlite and SqlServer.");
            }

            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning));
        });

        services.AddCors(options =>
        {
            options.AddPolicy(VirtualLabCorsOptions.PolicyName, policy =>
            {
                VirtualLabCorsOptions corsOptions = configuration
                    .GetSection(VirtualLabCorsOptions.SectionName)
                    .Get<VirtualLabCorsOptions>()
                    ?? new VirtualLabCorsOptions();

                string[] allowedOrigins = corsOptions.AllowedOrigins
                    .Select(origin => origin.Trim().TrimEnd('/'))
                    .Where(origin => !string.IsNullOrWhiteSpace(origin))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (allowedOrigins.Length == 0)
                {
                    return;
                }

                policy.WithOrigins(allowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
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

        // DOMS JPL TCP simulator
        services.Configure<DomsJplSimulatorOptions>(configuration.GetSection(DomsJplSimulatorOptions.SectionName));
        services.AddSingleton<DomsJplSimulatorState>();
        services.AddSingleton<DomsJplSimulatorService>();
        services.AddHostedService(sp => sp.GetRequiredService<DomsJplSimulatorService>());

        // Radix FDC HTTP/XML simulator (VL-4.2)
        services.Configure<RadixSimulatorOptions>(configuration.GetSection(RadixSimulatorOptions.SectionName));
        services.AddSingleton<RadixSimulatorState>();
        services.AddSingleton<RadixSimulatorService>();
        services.AddHostedService(sp => sp.GetRequiredService<RadixSimulatorService>());
        services.AddHttpClient("RadixSimulator", client => client.Timeout = TimeSpan.FromSeconds(10));

        // Petronite REST/JSON simulator (VL-4.3)
        services.Configure<PetroniteSimulatorOptions>(configuration.GetSection(PetroniteSimulatorOptions.SectionName));
        services.AddSingleton<PetroniteSimulatorState>();
        services.AddSingleton<PetroniteSimulatorService>();
        services.AddHostedService(sp => sp.GetRequiredService<PetroniteSimulatorService>());
        services.AddHttpClient("PetroniteSimulator", client => client.Timeout = TimeSpan.FromSeconds(10));

        return services;
    }
}
