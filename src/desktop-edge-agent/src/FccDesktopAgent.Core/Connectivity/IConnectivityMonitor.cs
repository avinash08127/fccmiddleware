namespace FccDesktopAgent.Core.Connectivity;

/// <summary>
/// Monitors both internet (cloud) and FCC LAN connectivity using dual independent probes.
/// Publishes connectivity state changes via <see cref="StateChanged"/>.
/// </summary>
public interface IConnectivityMonitor
{
    /// <summary>Current connectivity snapshot. Thread-safe read.</summary>
    ConnectivitySnapshot Current { get; }

    /// <summary>Raised when connectivity state transitions between values.</summary>
    event EventHandler<ConnectivitySnapshot>? StateChanged;
}
