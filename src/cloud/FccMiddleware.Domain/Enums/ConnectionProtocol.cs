namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Network protocol used to communicate with the FCC.
/// Stored as SCREAMING_SNAKE_CASE string in PostgreSQL.
/// </summary>
public enum ConnectionProtocol
{
    REST,
    TCP,
    SOAP
}
