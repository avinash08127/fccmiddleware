namespace FccMiddleware.Domain.Enums;

public enum DeadLetterType
{
    TRANSACTION,
    PRE_AUTH,
    TELEMETRY,
    UNKNOWN
}
