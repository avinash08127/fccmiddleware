using Microsoft.AspNetCore.Builder;

namespace FccDesktopAgent.App;

/// <summary>
/// Determines which window the Avalonia app shows on startup.
/// </summary>
internal enum StartupMode
{
    /// <summary>Device is registered — show MainWindow with diagnostics dashboard + tray icon.</summary>
    Normal,

    /// <summary>Device not yet registered — show ProvisioningWindow setup wizard.</summary>
    Provisioning,

    /// <summary>Device has been decommissioned — show dead-end DecommissionedWindow.</summary>
    Decommissioned,
}

/// <summary>
/// Bridge between the Generic Host DI container and the Avalonia Application instance.
/// Set before Avalonia initializes; read in App.axaml.cs during framework completion.
/// </summary>
internal static class AgentAppContext
{
    /// <summary>
    /// The service provider built from the web host. Set in Program.cs before Avalonia starts.
    /// Null only if the host failed to build (fatal startup error).
    /// </summary>
    public static IServiceProvider? ServiceProvider { get; set; }

    /// <summary>
    /// The WebApplication instance. Needed by ProvisioningWindow to call Start() after registration.
    /// </summary>
    public static WebApplication? WebApp { get; set; }

    /// <summary>
    /// Determines which window Avalonia shows on first launch.
    /// Set in Program.cs based on registration state.
    /// </summary>
    public static StartupMode Mode { get; set; } = StartupMode.Normal;
}
