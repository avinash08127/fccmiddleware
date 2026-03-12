namespace FccMiddleware.Domain.Enums;

public enum DeadLetterStatus
{
    PENDING,
    REPLAY_QUEUED,
    RETRYING,
    RESOLVED,
    REPLAY_FAILED,
    DISCARDED
}
