namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Real-time operational state of a pump nozzle as reported by the FCC or synthesized by the Edge Agent.
/// </summary>
public enum PumpState
{
    IDLE,
    AUTHORIZED,
    CALLING,
    DISPENSING,
    PAUSED,
    COMPLETED,
    ERROR,
    OFFLINE,
    UNKNOWN
}
