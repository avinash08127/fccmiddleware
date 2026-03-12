using System.Text.Json.Serialization;

namespace FccDesktopAgent.Core.Sync.Models;

/// <summary>
/// Response from GET /api/v1/transactions/synced-status.
/// Contains FCC transaction IDs acknowledged by Odoo since the requested timestamp.
/// </summary>
public sealed class SyncedStatusResponse
{
    [JsonPropertyName("fccTransactionIds")]
    public List<string> FccTransactionIds { get; init; } = [];
}
