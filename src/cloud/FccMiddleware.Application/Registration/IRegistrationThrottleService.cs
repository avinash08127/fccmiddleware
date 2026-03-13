namespace FccMiddleware.Application.Registration;

/// <summary>
/// Tracks failed registration attempts per IP and blocks IPs that exceed the threshold.
/// </summary>
public interface IRegistrationThrottleService
{
    /// <summary>Returns true if the IP has exceeded the maximum allowed failed attempts.</summary>
    Task<bool> IsBlockedAsync(string ipAddress, CancellationToken ct = default);

    /// <summary>Increments the failed-attempt counter for the IP.</summary>
    Task RecordFailedAttemptAsync(string ipAddress, CancellationToken ct = default);

    /// <summary>Resets the failed-attempt counter (e.g. after a successful registration).</summary>
    Task ResetAsync(string ipAddress, CancellationToken ct = default);
}
