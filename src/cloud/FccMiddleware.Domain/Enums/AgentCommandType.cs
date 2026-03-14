namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Operator-issued command types supported by the shared Android/Desktop agent control plane.
/// Serialized as SCREAMING_SNAKE_CASE strings over the API.
/// </summary>
public enum AgentCommandType
{
    FORCE_CONFIG_PULL,
    RESET_LOCAL_STATE,
    DECOMMISSION
}
