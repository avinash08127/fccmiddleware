using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FccDesktopAgent.Core.Runtime;

/// <summary>
/// Shared core service registration factory.
/// Both GUI (FccDesktopAgent.App) and headless (FccDesktopAgent.Service) call
/// <see cref="AddAgentCoreServices"/> before adding their own platform-specific wiring.
/// This ensures the two host modes are always in sync on what background workers run.
/// </summary>
public static class AgentHostBuilder
{
    /// <summary>
    /// Registers all core agent services on <paramref name="services"/>:
    ///   - <see cref="AgentDbContext"/> — SQLite buffer with WAL mode
    ///   - <see cref="CadenceController"/> — single coalesced cadence loop
    ///   - DEA-1.x stubs for IFccAdapter, IConnectivityMonitor, ICloudSyncService, etc.
    /// </summary>
    public static IServiceCollection AddAgentCoreServices(this IServiceCollection services)
    {
        // SQLite buffer — WAL mode enabled via interceptor (architecture rule #3)
        services.AddDbContext<AgentDbContext>(options =>
            options
                .UseSqlite(AgentDataDirectory.BuildConnectionString())
                .AddInterceptors(new SqliteWalModeInterceptor()));

        // Single coalesced background worker — architecture rule #10
        services.AddHostedService<CadenceController>();

        // DEA-1.x: Uncomment as concrete implementations are built.
        // services.AddSingleton<IConnectivityMonitor, ConnectivityMonitor>();
        // services.AddSingleton<IFccAdapter, DomsAdapter>();
        // services.AddSingleton<IIngestionOrchestrator, IngestionOrchestrator>();
        // services.AddSingleton<ICloudSyncService, CloudSyncService>();
        // services.AddSingleton<IPreAuthHandler, PreAuthHandler>();
        // services.AddSingleton<ICredentialStore, PlatformCredentialStore>();

        return services;
    }
}
