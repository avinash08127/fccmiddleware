namespace FccDesktopAgent.Core.Registration;

/// <summary>
/// Handles device registration against the cloud middleware.
/// Coordinates the HTTP call, credential storage, and state persistence.
/// </summary>
public interface IDeviceRegistrationService
{
    /// <summary>
    /// Registers this device with the cloud at the given base URL.
    /// On success: stores tokens in <see cref="Security.ICredentialStore"/>,
    /// persists identity via <see cref="IRegistrationManager"/>,
    /// and applies bootstrap <see cref="Config.SiteConfig"/> via <see cref="Config.IConfigManager"/>.
    /// </summary>
    Task<RegistrationResult> RegisterAsync(
        string cloudBaseUrl,
        DeviceRegistrationRequest request,
        CancellationToken ct = default);
}
