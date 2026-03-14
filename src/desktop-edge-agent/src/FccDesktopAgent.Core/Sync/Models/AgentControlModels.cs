using System.Text.Json;
using System.Text.Json.Serialization;
using FccDesktopAgent.Core.Security;

namespace FccDesktopAgent.Core.Sync.Models;

/// <summary>Shared cloud-to-agent command types for Android and desktop agents.</summary>
public enum AgentCommandType
{
    FORCE_CONFIG_PULL,
    RESET_LOCAL_STATE,
    DECOMMISSION
}

/// <summary>Lifecycle states returned by the authoritative cloud command plane.</summary>
public enum AgentCommandStatus
{
    PENDING,
    DELIVERY_HINT_SENT,
    ACKED,
    FAILED,
    EXPIRED,
    CANCELLED
}

/// <summary>Terminal outcomes that an agent may report when acknowledging a command.</summary>
public enum AgentCommandCompletionStatus
{
    ACKED,
    FAILED
}

/// <summary>
/// Response from GET /api/v1/agent/commands.
/// Polling is the source of truth even when Android push hints are enabled.
/// </summary>
public sealed class EdgeCommandPollResponse
{
    [JsonPropertyName("serverTimeUtc")]
    public DateTimeOffset ServerTimeUtc { get; init; }

    [JsonPropertyName("commands")]
    public List<EdgeCommandItem> Commands { get; init; } = [];
}

public sealed class EdgeCommandItem
{
    [JsonPropertyName("commandId")]
    public Guid CommandId { get; init; }

    [JsonPropertyName("commandType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandType CommandType { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandStatus Status { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Request body for POST /api/v1/agent/commands/{commandId}/ack.</summary>
public sealed class CommandAckRequest
{
    [JsonPropertyName("completionStatus")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandCompletionStatus CompletionStatus { get; init; }

    [JsonPropertyName("handledAtUtc")]
    public DateTimeOffset? HandledAtUtc { get; init; }

    [JsonPropertyName("failureCode")]
    public string? FailureCode { get; init; }

    [JsonPropertyName("failureMessage")]
    public string? FailureMessage { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }
}

/// <summary>Response body for POST /api/v1/agent/commands/{commandId}/ack.</summary>
public sealed class CommandAckResponse
{
    [JsonPropertyName("commandId")]
    public Guid CommandId { get; init; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentCommandStatus Status { get; init; }

    [JsonPropertyName("acknowledgedAt")]
    public DateTimeOffset AcknowledgedAt { get; init; }

    [JsonPropertyName("duplicate")]
    public bool Duplicate { get; init; }
}

/// <summary>
/// Android-only request body for POST /api/v1/agent/installations/android.
/// Included here so the desktop codebase shares the same frozen contract vocabulary.
/// </summary>
public sealed class AndroidInstallationUpsertRequest
{
    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; init; }

    [JsonPropertyName("registrationToken")]
    [SensitiveData]
    public required string RegistrationToken { get; init; }

    [JsonPropertyName("appVersion")]
    public required string AppVersion { get; init; }

    [JsonPropertyName("osVersion")]
    public required string OsVersion { get; init; }

    [JsonPropertyName("deviceModel")]
    public required string DeviceModel { get; init; }
}

/// <summary>Lowercase data-only push-hint kinds used by the Android FCM path.</summary>
public static class PushHintKinds
{
    public const string CommandPending = "command_pending";
    public const string ConfigChanged = "config_changed";
}
