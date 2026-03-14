using System.Text.Json.Serialization;

namespace FccMiddleware.Domain.Enums;

/// <summary>
/// Minimal push-hint kinds sent to Android devices.
/// Hints accelerate poll/config fetches only; they are not the source of truth.
/// </summary>
public enum PushHintKind
{
    [JsonStringEnumMemberName("command_pending")]
    COMMAND_PENDING,

    [JsonStringEnumMemberName("config_changed")]
    CONFIG_CHANGED
}
