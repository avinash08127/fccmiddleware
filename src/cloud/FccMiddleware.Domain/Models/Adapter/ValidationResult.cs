namespace FccMiddleware.Domain.Models.Adapter;

/// <summary>
/// Result of IFccAdapter.ValidatePayload. Structural and vendor-rule validation only —
/// no persistence or dedup checks. When IsValid=false normalization must not be attempted.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>False means normalization must not be attempted.</summary>
    public required bool IsValid { get; init; }

    /// <summary>
    /// Stable vendor-neutral error code. Null when IsValid=true.
    /// Standard codes: INVALID_JSON, MISSING_REQUIRED_FIELD, UNSUPPORTED_MESSAGE_TYPE,
    /// VENDOR_MISMATCH, NULL_PAYLOAD.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>Short diagnostic string for logging. Not exposed to callers.</summary>
    public string? Message { get; init; }

    /// <summary>
    /// True when retry with corrected payload or transient dependency may succeed.
    /// False for structural issues (malformed JSON, missing required fields).
    /// </summary>
    public required bool Recoverable { get; init; }

    public static ValidationResult Ok() => new() { IsValid = true, Recoverable = false };

    public static ValidationResult Fail(string errorCode, string message, bool recoverable = false) =>
        new() { IsValid = false, ErrorCode = errorCode, Message = message, Recoverable = recoverable };
}
