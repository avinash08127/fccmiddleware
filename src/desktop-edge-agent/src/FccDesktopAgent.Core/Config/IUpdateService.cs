namespace FccDesktopAgent.Core.Config;

/// <summary>
/// Abstraction for application auto-update functionality.
/// Implemented by Velopack in the App project; can be stubbed in tests or headless mode.
/// </summary>
public interface IUpdateService
{
    /// <summary>Whether the app is running from an installed (update-capable) context.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Checks for available updates and optionally downloads + applies them.
    /// Returns true if an update was downloaded and is ready to apply on next restart.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default);
}

/// <summary>Result of an update check.</summary>
public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string? AvailableVersion,
    bool Downloaded,
    string? ErrorMessage);
