namespace FccDesktopAgent.Core.Connectivity;

/// <summary>
/// Monitors both internet (cloud) and FCC LAN connectivity using dual independent probes.
/// Publishes connectivity state changes via <see cref="StateChanged"/>.
/// </summary>
public interface IConnectivityMonitor
{
    /// <summary>Current connectivity snapshot. Thread-safe read.</summary>
    ConnectivitySnapshot Current { get; }

    /// <summary>UTC timestamp of the last successful FCC heartbeat probe. Null if never connected.</summary>
    DateTimeOffset? LastFccSuccessAtUtc { get; }

    /// <summary>Current count of consecutive FCC probe failures (0 when FCC is healthy).</summary>
    int FccConsecutiveFailures { get; }

    /// <summary>Raised when connectivity state transitions between values.</summary>
    event EventHandler<ConnectivitySnapshot>? StateChanged;
}
