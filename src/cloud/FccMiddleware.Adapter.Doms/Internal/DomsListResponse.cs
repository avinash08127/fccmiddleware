using System.Text.Json.Serialization;

namespace FccMiddleware.Adapter.Doms.Internal;

/// <summary>
/// Deserialisation target for the DOMS GET /transactions response envelope.
/// Per §5.5: transactions[], optional nextCursor, optional hasMore, optional sourceBatchId.
/// </summary>
internal sealed class DomsListResponse
{
    [JsonPropertyName("transactions")]
    public List<DomsTransactionDto> Transactions { get; init; } = [];

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    [JsonPropertyName("sourceBatchId")]
    public string? SourceBatchId { get; init; }
}
