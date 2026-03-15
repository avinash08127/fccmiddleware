namespace FccDesktopAgent.Core.Registration;

/// <summary>
/// Manages the persisted device registration state (<c>registration.json</c>).
/// </summary>
public interface IRegistrationManager
{
    /// <summary>
    /// T-DSK-013: Returns whether the device is currently decommissioned.
    /// Backed by the cached registration state — no disk I/O on each call.
    /// Workers check this shared flag instead of maintaining independent volatile booleans.
    /// </summary>
    bool IsDecommissioned { get; }

    /// <summary>
    /// Returns whether the device is currently registered and should execute background work.
    /// </summary>
    bool IsRegistered { get; }

    /// <summary>
    /// Loads the current registration state from disk.
    /// Returns a default (unregistered) state if no file exists.
    /// </summary>
    RegistrationState LoadState();

    /// <summary>Persists the given registration state to disk.</summary>
    Task SaveStateAsync(RegistrationState state, CancellationToken ct = default);

    /// <summary>
    /// Sets <see cref="RegistrationState.IsDecommissioned"/> to <c>true</c> and persists.
    /// Called when the cloud returns 403 DEVICE_DECOMMISSIONED.
    /// </summary>
    Task MarkDecommissionedAsync(CancellationToken ct = default);

    /// <summary>
    /// Clears registration state so the provisioning wizard shows on next restart.
    /// Called when the refresh token has expired (401 from token refresh endpoint).
    /// Unlike decommission, re-provisioning can restore the device with a new bootstrap token.
    /// </summary>
    Task MarkReprovisioningRequiredAsync(CancellationToken ct = default);

    /// <summary>
    /// Raised when the device is marked as decommissioned (e.g. cloud returns 403 DEVICE_DECOMMISSIONED).
    /// GUI layers subscribe to this to show the decommission alert / dead-end window.
    /// </summary>
    event EventHandler? DeviceDecommissioned;

    /// <summary>
    /// M-10: Raised when the device requires re-provisioning (refresh token expired).
    /// GUI layers subscribe to show a re-provisioning prompt or restart the app.
    /// </summary>
    event EventHandler? ReprovisioningRequired;

    /// <summary>
    /// Syncs site equipment data from the cloud config to a local JSON file.
    /// Called after successful registration when the site config is available.
    /// </summary>
    Task SyncSiteDataAsync(SiteConfig config);
}
