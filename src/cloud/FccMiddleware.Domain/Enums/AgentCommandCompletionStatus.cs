namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Terminal status that an agent may report when acknowledging command handling.
/// </summary>
public enum AgentCommandCompletionStatus
{
    ACKED,
    FAILED
}
