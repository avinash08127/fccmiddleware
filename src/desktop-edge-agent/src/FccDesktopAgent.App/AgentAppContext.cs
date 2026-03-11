namespace FccDesktopAgent.App;

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
}
