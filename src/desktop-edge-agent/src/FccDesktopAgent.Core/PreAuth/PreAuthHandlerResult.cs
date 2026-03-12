using FccDesktopAgent.Core.Adapter.Common;
using PreAuthEntity = FccDesktopAgent.Core.Buffer.Entities.PreAuthRecord;

namespace FccDesktopAgent.Core.PreAuth;

/// <summary>
/// Result returned by <see cref="IPreAuthHandler"/> to the local API.
/// On success, carries the pre-auth record's ID, status, authorization code, and expiry.
/// On failure, carries the error code and optional detail message.
/// </summary>
public sealed record PreAuthHandlerResult
{
    public bool IsSuccess { get; init; }
    public PreAuthStatus? Status { get; init; }
    public string? RecordId { get; init; }
    public string? FccAuthorizationCode { get; init; }
    public string? FccCorrelationId { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public PreAuthHandlerError? Error { get; init; }
    public string? ErrorDetail { get; init; }

    // ── Factories ─────────────────────────────────────────────────────────────

    public static PreAuthHandlerResult Ok(PreAuthEntity record) => new()
    {
        IsSuccess = true,
        Status = record.Status,
        RecordId = record.Id,
        FccAuthorizationCode = record.FccAuthorizationCode,
        FccCorrelationId = record.FccCorrelationId,
        ExpiresAt = record.ExpiresAt,
    };

    public static PreAuthHandlerResult Fail(PreAuthHandlerError error, string? detail = null) => new()
    {
        IsSuccess = false,
        Error = error,
        ErrorDetail = detail,
    };
}
