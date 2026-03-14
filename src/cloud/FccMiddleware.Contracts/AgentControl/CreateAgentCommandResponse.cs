using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Contracts.AgentControl;

/// <summary>
/// Cloud response returned after a command has been persisted.
/// The created command ID is globally unique and becomes the idempotency key for all later acks.
/// </summary>
public sealed class CreateAgentCommandResponse
{
    public Guid CommandId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandType CommandType { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandStatus Status { get; set; }

    public string Reason { get; set; } = null!;
    public JsonElement? Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? CreatedByActorId { get; set; }
    public string? CreatedByActorDisplay { get; set; }
}
