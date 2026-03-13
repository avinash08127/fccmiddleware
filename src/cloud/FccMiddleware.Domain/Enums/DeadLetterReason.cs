namespace FccMiddleware.Domain.Enums;

public enum DeadLetterReason
{
    VALIDATION_FAILURE,
    NORMALIZATION_FAILURE,
    DEDUPLICATION_ERROR,
    ADAPTER_ERROR,
    PERSISTENCE_ERROR,
    UNKNOWN
}
