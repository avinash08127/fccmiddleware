namespace FccMiddleware.Domain.Enums;

public enum DeadLetterStatus
{
    PENDING,
    RETRYING,
    RESOLVED,
    DISCARDED
}
