using FccDesktopAgent.Api;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Runtime;
using FccDesktopAgent.Core.Security;
using FccDesktopAgent.Core.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

// Bootstrap logger captures startup failures before Serilog is fully configured.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        path: GetLogPath(),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateBootstrapLogger();

try
{
    Log.Information("FCC Desktop Agent Service starting");

    var builder = WebApplication.CreateBuilder(args);

    // Platform service lifetime: Windows Service / systemd / launchd (via startup wrapper)
    builder.Services.AddWindowsService(opts => opts.ServiceName = "FccDesktopAgent");
    builder.Services.AddSystemd();

    builder.Host.UseSerilog((_, cfg) => cfg
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Destructure.With<SensitiveDataDestructuringPolicy>()
        .WriteTo.Console()
        .WriteTo.File(path: GetLogPath(), rollingInterval: RollingInterval.Day));

    // Core agent services: database, HTTP clients, config binding, background workers
    builder.Services.AddAgentCore(builder.Configuration);

    // Local REST API — Kestrel on configurable port (default 8585), API key auth, 8 endpoints
    builder.Services.AddAgentApi(builder.Configuration);

    // Built-in health checks — answers GET /health with 200 Healthy
    builder.Services.AddHealthChecks();

    builder.WebHost.UseUrls($"http://0.0.0.0:{builder.Configuration["LocalApi:Port"] ?? "8585"}");

    var app = builder.Build();

    var commandExecutor = app.Services.GetService<IAgentCommandExecutor>();
    if (commandExecutor is not null)
        await commandExecutor.FinalizeAckedActionIfNeededAsync("startup", CancellationToken.None);

    // ── Registration gate ────────────────────────────────────────────────────
    var registrationManager = app.Services.GetRequiredService<IRegistrationManager>();
    var state = registrationManager.LoadState();

    if (state.IsDecommissioned)
    {
        Log.Fatal("Device has been decommissioned. Service cannot start. " +
                  "Contact your system administrator.");
        return 2;
    }

    if (!state.IsRegistered)
    {
        // Check for --register CLI argument for headless registration
        if (TryParseRegisterArgs(args, out var cloudUrl, out var siteCode, out var token))
        {
            Log.Information("Headless registration: registering with cloud at {CloudUrl}", cloudUrl);
            var regService = app.Services.GetRequiredService<IDeviceRegistrationService>();
            var request = DeviceInfoProvider.BuildRequest(token, siteCode);
            var result = await regService.RegisterAsync(cloudUrl, request);

            switch (result)
            {
                case RegistrationResult.Success success:
                    Log.Information("Headless registration successful — deviceId={DeviceId}",
                        success.Response.DeviceId);
                    break;

                case RegistrationResult.Rejected rejected:
                    Log.Fatal("Headless registration rejected: {ErrorCode} — {Message}",
                        rejected.Code, rejected.Message);
                    return 3;

                case RegistrationResult.TransportError transport:
                    Log.Fatal("Headless registration failed: {Message}", transport.Message);
                    return 3;
            }
        }
        else
        {
            Log.Fatal("Device is not registered. Register first using the GUI app, or use: " +
                      "--register --cloud-url <URL> --site-code <CODE> --provisioning-token <TOKEN>");
            return 3;
        }
    }

    app.MapHealthChecks("/health");
    app.MapLocalApi();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "FCC Desktop Agent Service terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

// ── Helpers ──────────────────────────────────────────────────────────────────

static bool TryParseRegisterArgs(string[] args, out string cloudUrl, out string siteCode, out string token)
{
    cloudUrl = siteCode = token = string.Empty;

    if (!args.Contains("--register", StringComparer.OrdinalIgnoreCase))
        return false;

    for (int i = 0; i < args.Length - 1; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--cloud-url":
                cloudUrl = args[++i];
                break;
            case "--site-code":
                siteCode = args[++i];
                break;
            case "--provisioning-token":
                token = args[++i];
                break;
        }
    }

    return !string.IsNullOrEmpty(cloudUrl) && !string.IsNullOrEmpty(siteCode) && !string.IsNullOrEmpty(token);
}

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
