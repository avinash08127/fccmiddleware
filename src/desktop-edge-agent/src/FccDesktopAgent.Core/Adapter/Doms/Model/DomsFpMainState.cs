using FccDesktopAgent.Core.Adapter.Common;

namespace FccDesktopAgent.Core.Adapter.Doms.Model;

/// <summary>
/// DOMS FP_MAIN_STATE values. Maps raw integer codes from the DOMS JPL protocol
/// to canonical <see cref="PumpState"/> via <see cref="DomsFpMainStateExtensions.ToCanonicalPumpState"/>.
/// </summary>
public enum DomsFpMainState
{
    FpInoperative = 0,
    FpClosed = 1,
    FpIdle = 2,
    FpCalling = 3,
    FpAuthorized = 4,
    FpStarted = 5,
    FpFuelling = 6,
    FpSuspended = 7,
    FpCompleted = 8,
    FpLocked = 9,
    FpError = 10,
    FpEmergencyStop = 11,
    FpDisconnected = 12,
    FpOffline = 13
}

public static class DomsFpMainStateExtensions
{
    /// <summary>
    /// Maps a DOMS FP_MAIN_STATE value to the canonical <see cref="PumpState"/> enum
    /// shared across all FCC adapter implementations.
    /// </summary>
    public static PumpState ToCanonicalPumpState(this DomsFpMainState state) => state switch
    {
        DomsFpMainState.FpInoperative    => PumpState.Offline,
        DomsFpMainState.FpClosed         => PumpState.Offline,
        DomsFpMainState.FpIdle           => PumpState.Idle,
        DomsFpMainState.FpCalling        => PumpState.Calling,
        DomsFpMainState.FpAuthorized     => PumpState.Authorized,
        DomsFpMainState.FpStarted        => PumpState.Authorized,
        DomsFpMainState.FpFuelling       => PumpState.Dispensing,
        DomsFpMainState.FpSuspended      => PumpState.Paused,
        DomsFpMainState.FpCompleted      => PumpState.Completed,
        DomsFpMainState.FpLocked         => PumpState.Offline,
        DomsFpMainState.FpError          => PumpState.Error,
        DomsFpMainState.FpEmergencyStop  => PumpState.Error,
        DomsFpMainState.FpDisconnected   => PumpState.Offline,
        DomsFpMainState.FpOffline        => PumpState.Offline,
        _                                => PumpState.Unknown,
    };
}
