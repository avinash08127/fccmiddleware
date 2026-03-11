namespace FccDesktopAgent.Core.Connectivity;

/// <summary>
/// Observable connectivity state for the desktop agent.
/// Both internet (cloud) and FCC LAN connectivity are tracked independently.
/// Transitions: 3 consecutive probe failures → DOWN; 1 success → UP (fast recovery).
/// </summary>
public enum ConnectivityState
{
    /// <summary>Both internet and FCC LAN are reachable. Full operation.</summary>
    FullyOnline,

    /// <summary>Internet is down but FCC LAN is reachable. Buffer locally, serve pre-auth via LAN.</summary>
    InternetDown,

    /// <summary>Internet is up but FCC LAN is unreachable. Upload existing buffer, no polling.</summary>
    FccUnreachable,

    /// <summary>Both internet and FCC LAN are unreachable. Serve stale buffer only.</summary>
    FullyOffline
}

/// <summary>Current connectivity snapshot published by <see cref="IConnectivityMonitor"/>.</summary>
public sealed record ConnectivitySnapshot(
    ConnectivityState State,
    bool IsInternetUp,
    bool IsFccUp,
    DateTimeOffset MeasuredAt);
