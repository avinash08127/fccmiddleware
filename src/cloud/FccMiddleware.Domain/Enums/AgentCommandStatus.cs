namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Lifecycle state of an agent command row in the authoritative cloud command store.
/// Serialized as SCREAMING_SNAKE_CASE strings over the API.
/// </summary>
public enum AgentCommandStatus
{
    PENDING,
    DELIVERY_HINT_SENT,
    ACKED,
    FAILED,
    EXPIRED,
    CANCELLED
}
