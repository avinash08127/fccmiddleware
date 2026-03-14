namespace FccDesktopAgent.Core.Config;

/// <summary>
/// Manages the agent's cloud-sourced configuration.
/// Follows the <c>IOptionsMonitor&lt;T&gt;</c> pattern: holds current config in memory,
/// signals changes via <see cref="Microsoft.Extensions.Options.IOptionsChangeTokenSource{T}"/>,
/// and overlays cloud values via <see cref="Microsoft.Extensions.Options.IPostConfigureOptions{T}"/>.
/// </summary>
public interface IConfigManager
{
    /// <summary>Current site config from cloud (null before first successful poll or DB load).</summary>
    SiteConfig? CurrentSiteConfig { get; }

    /// <summary>Current config version (ETag) for <c>If-None-Match</c> header.</summary>
    string? CurrentConfigVersion { get; }

    /// <summary>Whether a restart is pending due to restart-required config changes.</summary>
    bool RestartRequired { get; }

    /// <summary>Raised when config changes are applied.</summary>
    event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    /// <summary>
    /// Validate and apply a new config received from cloud.
    /// Stores in database, applies hot-reload fields via the Options infrastructure,
    /// and flags restart-required changes.
    /// </summary>
    Task<ConfigApplyResult> ApplyConfigAsync(
        SiteConfig newConfig, string rawJson, string configVersion, CancellationToken ct);

    /// <summary>Load last-known-good config from database on startup.</summary>
    Task LoadFromDatabaseAsync(CancellationToken ct);

    /// <summary>
    /// Clears the in-memory and persisted cloud config snapshot so the agent
    /// re-enters an unconfigured/provisioning state without a process restart.
    /// </summary>
    Task ResetAsync(CancellationToken ct);
}
