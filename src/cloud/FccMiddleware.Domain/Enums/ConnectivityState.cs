namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Edge Agent connectivity state derived from two independent probes:
/// internet health ping and FCC heartbeat.
/// </summary>
public enum ConnectivityState
{
    FULLY_ONLINE,
    INTERNET_DOWN,
    FCC_UNREACHABLE,
    FULLY_OFFLINE
}
