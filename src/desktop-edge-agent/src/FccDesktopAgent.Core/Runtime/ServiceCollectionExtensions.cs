using FccDesktopAgent.Core.Adapter;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Interceptors;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Ingestion;
using FccDesktopAgent.Core.PreAuth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FccDesktopAgent.Core.Runtime;

/// <summary>
/// Core dependency injection extension methods shared by both entry points:
/// <c>FccDesktopAgent.App</c> (GUI) and <c>FccDesktopAgent.Service</c> (headless).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all core agent services: database, named HTTP clients, configuration binding,
    /// and background workers. This is the primary entry point — both host modes call this.
    /// </summary>
    public static IServiceCollection AddAgentCore(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddAgentDatabase(config);
        services.AddAgentBufferServices();
        services.AddAgentConnectivity();
        services.AddAgentBackgroundWorkers();

        // Bind AgentConfiguration from the "Agent" configuration section
        services.Configure<AgentConfiguration>(config.GetSection("Agent"));

        // Named HTTP client for FCC LAN calls — short timeout, station-local only
        services.AddHttpClient("fcc", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Named HTTP client for cloud backend calls — longer timeout, internet-facing
        services.AddHttpClient("cloud", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // FCC adapter factory (DEA-2.5: pre-auth handler support)
        services.AddSingleton<IFccAdapterFactory, FccAdapterFactory>();

        // Pre-auth handler — scoped (depends on AgentDbContext which is scoped)
        services.AddScoped<IPreAuthHandler, PreAuthHandler>();

        // DEA-2.6: Ingestion orchestrator — singleton, uses IServiceScopeFactory for scoped deps.
        services.AddSingleton<IIngestionOrchestrator, IngestionOrchestrator>();

        // DEA-1.x: Uncomment as concrete implementations are built.
        // services.AddSingleton<ICloudSyncService, CloudSyncService>();
        // services.AddSingleton<ICredentialStore, PlatformCredentialStore>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="AgentDbContext"/> backed by SQLite in WAL mode (architecture rule #3).
    /// The data directory is resolved cross-platform via <see cref="AgentDataDirectory"/>.
    /// </summary>
    public static IServiceCollection AddAgentDatabase(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddDbContext<AgentDbContext>(options =>
            options
                .UseSqlite(AgentDataDirectory.BuildConnectionString())
                .AddInterceptors(new SqliteWalModeInterceptor()));

        return services;
    }

    /// <summary>
    /// Registers buffer management services: <see cref="TransactionBufferManager"/>
    /// and <see cref="IntegrityChecker"/>.
    /// </summary>
    public static IServiceCollection AddAgentBufferServices(
        this IServiceCollection services)
    {
        services.AddScoped<TransactionBufferManager>();
        services.AddScoped<IntegrityChecker>();
        return services;
    }

    /// <summary>
    /// Registers the <see cref="ConnectivityManager"/> as both
    /// <see cref="IConnectivityMonitor"/> (for consumers) and
    /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> (to start the probe loop).
    /// The singleton is shared so consumers always see the live state.
    /// </summary>
    public static IServiceCollection AddAgentConnectivity(
        this IServiceCollection services)
    {
        // Single instance shared between interface consumers and the hosted service lifecycle.
        services.AddSingleton<ConnectivityManager>();
        services.AddSingleton<IConnectivityMonitor>(sp => sp.GetRequiredService<ConnectivityManager>());
        services.AddHostedService(sp => sp.GetRequiredService<ConnectivityManager>());
        return services;
    }

    /// <summary>
    /// Registers all <see cref="Microsoft.Extensions.Hosting.IHostedService"/> background workers.
    /// The single <see cref="CadenceController"/> coalesces all recurring work (architecture rule #10).
    /// Do not add independent timer loops here — extend <see cref="CadenceController"/> instead.
    /// </summary>
    public static IServiceCollection AddAgentBackgroundWorkers(
        this IServiceCollection services)
    {
        // CadenceController must start AFTER ConnectivityManager (registered in AddAgentConnectivity).
        // Generic Host starts IHostedServices in registration order.
        services.AddHostedService<CadenceController>();
        services.AddHostedService<CleanupWorker>();

        // DEA-1.x: Additional hosted services registered here as they are implemented.
        return services;
    }
}
