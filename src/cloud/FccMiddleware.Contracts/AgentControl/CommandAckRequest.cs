using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Contracts.AgentControl;

/// <summary>
/// Agent acknowledgement for a previously-issued command.
/// Acks are idempotent: repeating the same terminal outcome for the same command is safe.
/// </summary>
public sealed class CommandAckRequest
{
    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandCompletionStatus CompletionStatus { get; set; }

    /// <summary>
    /// Optional agent-observed completion time in UTC. Null means "cloud receipt time".
    /// </summary>
    public DateTimeOffset? HandledAtUtc { get; set; }

    [StringLength(100)]
    public string? FailureCode { get; set; }

    [StringLength(1000)]
    public string? FailureMessage { get; set; }

    /// <summary>
    /// Optional non-sensitive execution summary. Never include local credentials,
    /// raw config content, customer identifiers, or registration tokens.
    /// </summary>
    public JsonElement? Result { get; set; }
}
