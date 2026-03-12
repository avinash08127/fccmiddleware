namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Thrown when the cloud returns HTTP 403 with DEVICE_DECOMMISSIONED.
/// Signals that cloud sync must be halted and the supervisor notified.
/// </summary>
public sealed class DeviceDecommissionedException : Exception
{
    public DeviceDecommissionedException(string reason)
        : base($"Device has been decommissioned by the cloud: {reason}") { }
}
