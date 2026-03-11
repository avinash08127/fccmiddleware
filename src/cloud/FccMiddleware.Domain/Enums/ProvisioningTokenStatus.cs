namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Lifecycle state of a one-time provisioning token used to register an Edge Agent.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum ProvisioningTokenStatus
{
    ACTIVE,
    USED,
    EXPIRED,
    REVOKED
}
