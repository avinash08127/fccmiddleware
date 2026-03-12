namespace FccDesktopAgent.Core.Registration;

/// <summary>
/// Manages the persisted device registration state (<c>registration.json</c>).
/// </summary>
public interface IRegistrationManager
{
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
}
