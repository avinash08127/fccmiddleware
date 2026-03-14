using Avalonia;
using FccDesktopAgent.Api;
using FccDesktopAgent.Api.Endpoints;
using FccDesktopAgent.App;
using FccDesktopAgent.App.Services;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Runtime;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync;
using FccDesktopAgent.Core.WebSocket;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
    var apiPort = int.TryParse(builder.Configuration["LocalApi:Port"], out var parsedApiPort) ? parsedApiPort : 8585;

    // S-DSK-002 + S-DSK-005: Kestrel endpoint configuration uses IConfigureOptions
    // so the WebSocket TLS certificate password can be read from ICredentialStore
    // instead of plaintext config. The password variable is scoped to
    // ConfigureWebSocketTls() to prevent accidental logging.
    builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>>(sp =>
        new ConfigureOptions<KestrelServerOptions>(options =>
        {
            options.ListenAnyIP(apiPort);

            if (wsEnabled && wsPort != apiPort)
                ConfigureWebSocketTls(options, wsPort, wsUseTls, wsCertPath, wsConfig,
                    sp.GetService<ICredentialStore>());
        }));

    var webApp = builder.Build();
    webApp.UseWebSockets();
    webApp.MapOdooWebSocket();
    webApp.MapHealthChecks("/health");
    webApp.MapLocalApi();

    // Expose the DI container and WebApp to Avalonia
    AgentAppContext.ServiceProvider = webApp.Services;
    AgentAppContext.WebApp = webApp;

    var commandExecutor = webApp.Services.GetService<IAgentCommandExecutor>();
    if (commandExecutor is not null)
        await commandExecutor.FinalizeAckedActionIfNeededAsync("startup", CancellationToken.None);

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
        AgentAppContext.IsHostStarted = true;
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
        await webApp.StopAsync();
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
            await webApp.StopAsync();
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
    await Log.CloseAndFlushAsync();
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

// S-DSK-002 + S-DSK-005: Dedicated method for WebSocket TLS configuration.
// Certificate password is scoped to this method — do NOT log it.
static void ConfigureWebSocketTls(
    KestrelServerOptions options,
    int wsPort,
    bool wsUseTls,
    string? wsCertPath,
    IConfigurationSection wsConfig,
    ICredentialStore? credentialStore)
{
    if (wsUseTls && !string.IsNullOrEmpty(wsCertPath))
    {
        // S-DSK-002: Read certificate password from credential store (preferred)
        // or fall back to config file. Do NOT log this variable.
        string? certPassword = null;
        if (credentialStore is not null)
        {
            try { certPassword = credentialStore.GetSecretAsync(CredentialKeys.WsCertPassword).GetAwaiter().GetResult(); }
            catch { /* credential store unavailable — fall through to config */ }
        }
        certPassword ??= wsConfig.GetValue<string?>("CertificatePassword");

        options.ListenAnyIP(wsPort, listenOptions =>
        {
            listenOptions.UseHttps(wsCertPath, certPassword ?? "");
            Log.Information("WebSocket TLS endpoint configured on port {Port} with certificate {Cert}", wsPort, wsCertPath);
        });
    }
    else
    {
        options.ListenAnyIP(wsPort);
        Log.Information("WebSocket endpoint configured on port {Port} (no TLS)", wsPort);
    }
}
