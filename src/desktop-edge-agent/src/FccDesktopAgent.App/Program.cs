using Avalonia;
using FccDesktopAgent.Api;
using FccDesktopAgent.App;
using FccDesktopAgent.App.Services;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

    builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration["LocalApi:Port"] ?? "8585"}");

    var webApp = builder.Build();
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

    // After provisioning + normal operation, stop the host if it was started
    if (registrationManager.LoadState().IsRegistered)
    {
        Log.Information("Avalonia exited (code {ExitCode}), stopping host", provisioningExitCode);
        webApp.StopAsync().GetAwaiter().GetResult();
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
