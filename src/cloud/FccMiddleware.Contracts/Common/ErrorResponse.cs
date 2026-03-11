namespace FccMiddleware.Contracts.Common;

/// <summary>
/// Standard error response envelope per tier-2-1-error-handling-strategy.md.
/// Returned on 400, 401, 409, 422, 429, and 500 responses.
/// </summary>
public sealed record ErrorResponse
{
    /// <summary>Hierarchical stable error code (e.g., VALIDATION.MISSING_FIELD, CONFLICT.DUPLICATE_TRANSACTION).</summary>
    public required string ErrorCode { get; init; }

    /// <summary>Human-readable summary of the error.</summary>
    public required string Message { get; init; }

    /// <summary>Structured context for the error (field name, values, etc.). Null when no detail is available.</summary>
    public object? Details { get; init; }

    /// <summary>OpenTelemetry trace ID for correlation with logs.</summary>
    public required string TraceId { get; init; }

    /// <summary>ISO 8601 UTC timestamp when the error occurred.</summary>
    public required string Timestamp { get; init; }

    /// <summary>Whether the caller should retry this request.</summary>
    public bool Retryable { get; init; }
}
