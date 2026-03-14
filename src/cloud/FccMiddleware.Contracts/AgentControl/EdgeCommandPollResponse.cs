using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Contracts.AgentControl;

/// <summary>
/// Authenticated agent poll response for the shared command plane.
/// Polling remains the source of truth even when Android push hints are enabled.
/// </summary>
public sealed class EdgeCommandPollResponse
{
    public DateTimeOffset ServerTimeUtc { get; set; }
    public IReadOnlyList<EdgeCommandPollItem> Commands { get; set; } = Array.Empty<EdgeCommandPollItem>();
}

public sealed class EdgeCommandPollItem
{
    public Guid CommandId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandType CommandType { get; set; }

    /// <summary>
    /// Commands returned by the poll endpoint are actionable only while status is
    /// PENDING or DELIVERY_HINT_SENT and the command has not expired.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandStatus Status { get; set; }

    public string Reason { get; set; } = null!;
    public JsonElement? Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
