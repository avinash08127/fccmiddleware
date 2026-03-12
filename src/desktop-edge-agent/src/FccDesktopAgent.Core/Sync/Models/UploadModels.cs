using System.Text.Json.Serialization;
using FccDesktopAgent.Core.Adapter.Common;

namespace FccDesktopAgent.Core.Sync.Models;

/// <summary>
/// Request payload for POST /api/v1/transactions/upload.
/// Transactions must be ordered by CompletedAt ASC (oldest first).
/// </summary>
public sealed class UploadRequest
{
    [JsonPropertyName("transactions")]
    public List<CanonicalTransaction> Transactions { get; init; } = [];
}

/// <summary>
/// Response from POST /api/v1/transactions/upload.
/// HTTP 200 is always returned if the batch was processed, even if some records were rejected.
/// </summary>
public sealed class UploadResponse
{
    [JsonPropertyName("results")]
    public List<UploadResultItem> Results { get; init; } = [];

    [JsonPropertyName("acceptedCount")]
    public int AcceptedCount { get; init; }

    [JsonPropertyName("duplicateCount")]
    public int DuplicateCount { get; init; }

    [JsonPropertyName("rejectedCount")]
    public int RejectedCount { get; init; }
}

/// <summary>
/// Per-record result from the cloud upload endpoint.
/// Outcome: ACCEPTED | DUPLICATE | REJECTED.
/// </summary>
public sealed class UploadResultItem
{
    [JsonPropertyName("fccTransactionId")]
    public string FccTransactionId { get; init; } = string.Empty;

    [JsonPropertyName("siteCode")]
    public string SiteCode { get; init; } = string.Empty;

    /// <summary>ACCEPTED, DUPLICATE, or REJECTED.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Cloud-assigned UUID. Set when outcome is ACCEPTED.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Error detail. Set when outcome is REJECTED.</summary>
    [JsonPropertyName("error")]
    public UploadResultError? Error { get; init; }
}

public sealed class UploadResultError
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Response from POST /api/v1/agent/token/refresh.
/// Both tokens are rotated on every successful refresh (per spec).
/// </summary>
public sealed class TokenRefreshResponse
{
    [JsonPropertyName("deviceToken")]
    public string? DeviceToken { get; init; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("tokenExpiresAt")]
    public DateTimeOffset? TokenExpiresAt { get; init; }
}
