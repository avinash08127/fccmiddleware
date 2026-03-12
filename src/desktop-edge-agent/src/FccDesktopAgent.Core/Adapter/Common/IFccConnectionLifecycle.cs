namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Optional lifecycle interface for FCC adapters that maintain persistent connections.
///
/// Implemented by DOMS TCP/JPL adapter (persistent TCP socket with heartbeat).
/// NOT implemented by Radix (stateless HTTP) or Petronite (stateless REST + webhook).
///
/// The runtime controller checks <c>adapter is IFccConnectionLifecycle</c> at runtime:
///   - If true: calls ConnectAsync on startup, DisconnectAsync on shutdown, wires event listener
///   - If false: skips lifecycle management entirely (adapter handles its own connections)
/// </summary>
public interface IFccConnectionLifecycle
{
    /// <summary>
    /// Establish the persistent connection to the FCC.
    /// For DOMS: opens TCP socket, completes FcLogon handshake, starts heartbeat timer.
    /// Must be called before any IFccAdapter operations.
    /// </summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Gracefully close the persistent connection.
    /// Stops heartbeat, sends disconnect message if supported, closes socket.
    /// Idempotent — safe to call when already disconnected.
    /// </summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether the persistent connection is currently alive and authenticated.
    /// For DOMS: true if TCP socket is open AND FcLogon succeeded AND
    /// last heartbeat response was within 3x heartbeat interval.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Register a callback listener for unsolicited FCC events.
    /// Set to null to unregister.
    /// </summary>
    void SetEventListener(IFccEventListener? listener);
}
