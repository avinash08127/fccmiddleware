using System.Text.Json.Serialization;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Contracts.AgentControl;

/// <summary>
/// Cloud acknowledgement result for a command completion report.
/// </summary>
public sealed class CommandAckResponse
{
    public Guid CommandId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandStatus Status { get; set; }

    public DateTimeOffset AcknowledgedAt { get; set; }

    /// <summary>
    /// True when the same terminal ack had already been stored earlier and this call
    /// only replayed the existing result.
    /// </summary>
    public bool Duplicate { get; set; }
}
