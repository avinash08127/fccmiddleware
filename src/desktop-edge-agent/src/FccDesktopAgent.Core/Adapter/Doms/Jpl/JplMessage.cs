using System.Text.Json.Serialization;

namespace FccDesktopAgent.Core.Adapter.Doms.Jpl;

/// <summary>
/// JPL protocol message with name, optional sub-code, and data map.
/// Serialized as JSON inside a binary STX/ETX frame by <see cref="JplFrameCodec"/>.
/// </summary>
public sealed record JplMessage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("subCode")] int? SubCode = null,
    [property: JsonPropertyName("data")] IReadOnlyDictionary<string, string>? Data = null);
