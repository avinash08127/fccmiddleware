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

    /// <summary>Batch-level idempotency key. Cloud caches results keyed by this ID.</summary>
    [JsonPropertyName("uploadBatchId")]
    public string? UploadBatchId { get; init; }
}

/// <summary>
/// Response from POST /api/v1/transactions/upload.
/// HTTP 200 is always returned if the batch was processed, even if some records were rejected.
/// Aligned with cloud contract: FccMiddleware.Contracts.Ingestion.UploadResponse.
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
/// Aligned with cloud contract: FccMiddleware.Contracts.Ingestion.UploadRecordResult.
/// Outcome: ACCEPTED | DUPLICATE | REJECTED.
/// </summary>
public sealed class UploadResultItem
{
    /// <summary>FCC transaction ID echoed back from the request.</summary>
    [JsonPropertyName("fccTransactionId")]
    public string FccTransactionId { get; init; } = string.Empty;

    /// <summary>ACCEPTED, DUPLICATE, or REJECTED.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Cloud-assigned UUID for accepted transactions. Null for duplicates and rejections.</summary>
    [JsonPropertyName("transactionId")]
    public Guid? TransactionId { get; init; }

    /// <summary>ID of the original transaction when Outcome is DUPLICATE.</summary>
    [JsonPropertyName("originalTransactionId")]
    public Guid? OriginalTransactionId { get; init; }

    /// <summary>Structured error code for REJECTED records.</summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable error detail for REJECTED records.</summary>
    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }
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
