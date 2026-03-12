using Avalonia;
using FccDesktopAgent.Api;
using FccDesktopAgent.Api.Endpoints;
using FccDesktopAgent.App;
using FccDesktopAgent.App.Services;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Runtime;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Velopack;

// ── Velopack lifecycle hooks — MUST be the very first thing ─────────────────
// Handles install/uninstall/update events before any other code runs.
VelopackApp.Build().Run();

// Bootstrap logger captures startup failures before Serilog is fully configured.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.File(
        path: GetLogPath(),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateBootstrapLogger();

try
{
    Log.Information("FCC Desktop Agent (GUI) starting");

    // ── Build the web / generic host ──────────────────────────────────────────
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((_, cfg) => cfg
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Destructure.With<SensitiveDataDestructuringPolicy>()
        .WriteTo.Console()
        .WriteTo.File(path: GetLogPath(), rollingInterval: RollingInterval.Day));

    // Core agent services: database, HTTP clients, config binding, background workers
    builder.Services.AddAgentCore(builder.Configuration);

    // Local REST API — Kestrel on configurable port (default 8585), API key auth, 8 endpoints
    builder.Services.AddAgentApi(builder.Configuration);

    // Auto-update service — Velopack-backed
    builder.Services.AddSingleton<IUpdateService, VelopackUpdateService>();

    // Built-in health checks — answers GET /health with 200 Healthy
    builder.Services.AddHealthChecks();

    // ── Kestrel endpoint configuration ────────────────────────────────────────
    // Primary: REST API port (HTTP). Secondary (optional): WebSocket port with optional TLS.
    var wsConfig = builder.Configuration.GetSection(WebSocketServerOptions.SectionName);
    var wsEnabled = wsConfig.GetValue<bool>("Enabled");
    var wsPort = wsConfig.GetValue<int?>("Port") ?? 8443;
    var wsUseTls = wsConfig.GetValue<bool>("UseTls");
    var wsCertPath = wsConfig.GetValue<string?>("CertificatePath");
    var wsCertPassword = wsConfig.GetValue<string?>("CertificatePassword");
    var apiPort = int.TryParse(builder.Configuration["LocalApi:Port"], out var parsedApiPort) ? parsedApiPort : 8585;

    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        // REST API — always HTTP
        serverOptions.ListenAnyIP(apiPort);

        // WebSocket — separate port if enabled and different from API port
        if (wsEnabled && wsPort != apiPort)
        {
            if (wsUseTls && !string.IsNullOrEmpty(wsCertPath))
            {
                serverOptions.ListenAnyIP(wsPort, listenOptions =>
                {
                    listenOptions.UseHttps(wsCertPath, wsCertPassword ?? "");
                    Log.Information("WebSocket TLS endpoint configured on port {Port} with certificate {Cert}", wsPort, wsCertPath);
                });
            }
            else
            {
                serverOptions.ListenAnyIP(wsPort);
                Log.Information("WebSocket endpoint configured on port {Port} (no TLS)", wsPort);
            }
        }
    });

    var webApp = builder.Build();
    webApp.UseWebSockets();
    webApp.MapOdooWebSocket();
    webApp.MapHealthChecks("/health");
    webApp.MapLocalApi();

    // Expose the DI container and WebApp to Avalonia
    AgentAppContext.ServiceProvider = webApp.Services;
    AgentAppContext.WebApp = webApp;

    // ── Registration gate — route based on device state ──────────────────────
    var registrationManager = webApp.Services.GetRequiredService<IRegistrationManager>();
    var registrationState = registrationManager.LoadState();

    if (registrationState.IsDecommissioned)
    {
        // Dead end: show decommission window, never start services
        Log.Warning("Device is decommissioned — showing decommission screen");
        AgentAppContext.Mode = StartupMode.Decommissioned;
        return RunAvalonia(args);
    }

    if (registrationState.IsRegistered)
    {
        // Validate runtime config is complete for the selected ingestion mode
        var resolvedConfig = webApp.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentConfiguration>>().Value;
        var configError = AgentConfigurationValidator.Validate(resolvedConfig);
        if (configError is not null)
            Log.Warning("Startup config validation: {Error} — agent may not function correctly until config is received from cloud", configError);

        // Normal operational mode — start all services, then show dashboard
        webApp.Start();
        Log.Information("FCC Desktop Agent host started — listening on port 8585");

        // Non-blocking startup update check
        _ = Task.Run(async () =>
        {
            try
            {
                var updateService = webApp.Services.GetRequiredService<IUpdateService>();
                var result = await updateService.CheckForUpdatesAsync();
                if (result.UpdateAvailable && result.Downloaded)
                    Log.Information("Update {Version} staged — will apply on next restart", result.AvailableVersion);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Startup update check failed (non-fatal)");
            }
        });

        AgentAppContext.Mode = StartupMode.Normal;
        var exitCode = RunAvalonia(args);

        // Graceful shutdown after Avalonia exits
        Log.Information("Avalonia exited (code {ExitCode}), stopping host", exitCode);
        webApp.StopAsync().GetAwaiter().GetResult();
        return exitCode;
    }

    // Not registered — show provisioning wizard first
    Log.Information("Device not registered — showing provisioning wizard");
    AgentAppContext.Mode = StartupMode.Provisioning;
    var provisioningExitCode = RunAvalonia(args);

    // L-05: After provisioning + normal operation, stop the host if it was started.
    // Wrap in try-catch so partial host starts don't leave orphaned resources.
    if (registrationManager.LoadState().IsRegistered)
    {
        Log.Information("Avalonia exited (code {ExitCode}), stopping host", provisioningExitCode);
        try
        {
            webApp.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Host cleanup failed during provisioning shutdown — process will exit");
        }
    }

    return provisioningExitCode;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "FCC Desktop Agent (GUI) terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlushAsync().GetAwaiter().GetResult();
}

// ── Helpers ──────────────────────────────────────────────────────────────────

static int RunAvalonia(string[] args) =>
    AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace()
        .StartWithClassicDesktopLifetime(args);

static string GetLogPath()
{
    var logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FccDesktopAgent",
        "logs",
        "agent-.log");
    Directory.CreateDirectory(Path.GetDirectoryName(logDir)!);
    return logDir;
}
