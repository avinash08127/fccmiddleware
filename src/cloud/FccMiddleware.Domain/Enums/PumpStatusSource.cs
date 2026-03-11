namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Indicates whether pump status was received live from the FCC or synthesized by the Edge Agent.
/// </summary>
public enum PumpStatusSource
{
    FCC_LIVE,
    EDGE_SYNTHESIZED
}
