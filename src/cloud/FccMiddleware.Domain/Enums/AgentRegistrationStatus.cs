namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Lifecycle state of an Edge Agent registration record.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum AgentRegistrationStatus
{
    ACTIVE,
    DEACTIVATED
}
