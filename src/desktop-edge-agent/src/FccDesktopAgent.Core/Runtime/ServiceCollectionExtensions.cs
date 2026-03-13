using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FccDesktopAgent.Core.Adapter;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Interceptors;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Ingestion;
using FccDesktopAgent.Core.PreAuth;
using FccDesktopAgent.Core.MasterData;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync;
using FccDesktopAgent.Core.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // DEA-3.5: Platform-specific secure credential storage (DPAPI / Keychain / libsecret).
        services.AddSingleton<ICredentialStore, PlatformCredentialStore>();

        // Local FCC override manager — reads/writes overrides.json for on-site config tuning.
        services.AddSingleton<LocalOverrideManager>();

        // Site data persistence — extracts products/pumps/nozzles from config to JSON.
        services.AddSingleton<SiteDataManager>();

        // DEA-3.5: Registration state manager — also overlays identity (DeviceId, SiteId, CloudBaseUrl)
        // onto AgentConfiguration via IPostConfigureOptions so workers always see the current identity.
        services.AddSingleton<RegistrationManager>();
        services.AddSingleton<IRegistrationManager>(sp => sp.GetRequiredService<RegistrationManager>());
        services.AddSingleton<IPostConfigureOptions<AgentConfiguration>>(
            sp => sp.GetRequiredService<RegistrationManager>());

        // DEA-3.5: Device registration service.
        services.AddSingleton<IDeviceRegistrationService, DeviceRegistrationService>();

        // Bind AgentConfiguration from the "Agent" configuration section
        services.Configure<AgentConfiguration>(config.GetSection("Agent"));

        // Named HTTP client for FCC LAN calls — short timeout, station-local only
        services.AddHttpClient("fcc", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Named HTTP client for cloud backend calls — longer timeout, internet-facing.
        // DEA-6.2: Enforce TLS for all cloud communication.
        // MISSING-007: Certificate pinning — matches Android agent's bootstrap pins.
        services.AddHttpClient("cloud", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // Enforce TLS 1.2+ for all cloud communication (architecture rule: TLS enforced)
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                         | System.Security.Authentication.SslProtocols.Tls13,
            ServerCertificateCustomValidationCallback = CertificatePinValidator.Validate,
        });

        // FCC adapter factory (DEA-2.5: pre-auth handler support)
        services.AddSingleton<IFccAdapterFactory, FccAdapterFactory>();

        // Pump status service — single-flight protection + stale cache (architecture rule #13)
        services.AddSingleton<IPumpStatusService, PumpStatusService>();

        // Pre-auth handler — scoped (depends on AgentDbContext which is scoped)
        services.AddScoped<IPreAuthHandler, PreAuthHandler>();

        // DEA-2.6: Ingestion orchestrator — singleton, uses IServiceScopeFactory for scoped deps.
        services.AddSingleton<IIngestionOrchestrator, IngestionOrchestrator>();

        // DEA-3.1: Cloud upload pipeline.
        services.AddSingleton<IDeviceTokenProvider, DeviceTokenProvider>();
        // T-DSK-010: Shared auth handler eliminates duplicated 401/refresh/retry logic across workers.
        services.AddSingleton<AuthenticatedCloudRequestHandler>();
        services.AddSingleton<ICloudSyncService, CloudUploadWorker>();

        // DEA-3.2: SYNCED_TO_ODOO status poller.
        services.AddSingleton<ISyncedToOdooPoller, StatusPollWorker>();

        // DEA-3.3: Config manager and config poll worker.
        // ConfigManager is shared across IConfigManager, IOptionsChangeTokenSource, and IPostConfigureOptions
        // so that cloud config changes flow through the .NET Options infrastructure (IOptionsMonitor).
        services.AddSingleton<ConfigManager>();
        services.AddSingleton<IConfigManager>(sp => sp.GetRequiredService<ConfigManager>());
        services.AddSingleton<IOptionsChangeTokenSource<AgentConfiguration>>(
            sp => sp.GetRequiredService<ConfigManager>());
        services.AddSingleton<IPostConfigureOptions<AgentConfiguration>>(
            sp => sp.GetRequiredService<ConfigManager>());
        services.AddSingleton<IConfigPoller, ConfigPollWorker>();
        // T-DSK-016: Config save service — encapsulates SiteConfig construction and apply orchestration.
        services.AddSingleton<ConfigSaveService>();

        // DEA-3.4: Telemetry reporter and error tracker.
        services.AddSingleton<IErrorCountTracker, ErrorCountTracker>();
        services.AddSingleton<ITelemetryReporter, TelemetryReporter>();

        // Version checker — called on startup to validate agent compatibility with cloud.
        services.AddSingleton<IVersionChecker, VersionCheckService>();

        // Odoo backward-compat WebSocket server.
        services.AddSingleton<OdooWebSocketServer>();
        services.Configure<WebSocketServerOptions>(config.GetSection(WebSocketServerOptions.SectionName));

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
        // T-DSK-014: Transaction update service — shared by WebSocket handler and REST API.
        services.AddScoped<ITransactionUpdateService, TransactionUpdateService>();
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
