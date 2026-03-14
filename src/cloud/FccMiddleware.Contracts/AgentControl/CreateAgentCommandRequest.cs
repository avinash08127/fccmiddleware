using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Contracts.AgentControl;

/// <summary>
/// Portal/admin request to create a new command for a specific registered agent.
/// Payload is optional and must remain non-sensitive.
/// </summary>
public sealed class CreateAgentCommandRequest
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandType CommandType { get; set; }

    [Required]
    [StringLength(500, MinimumLength = 3)]
    public string Reason { get; set; } = null!;

    /// <summary>
    /// Optional command-specific metadata. Must not contain secrets, tokens, credentials,
    /// raw config documents, or customer PII.
    /// </summary>
    public JsonElement? Payload { get; set; }

    /// <summary>
    /// Optional UTC expiry override. Null means "use the server default TTL".
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
