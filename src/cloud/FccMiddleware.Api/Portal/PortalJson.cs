using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Domain.Entities;

namespace FccMiddleware.Api.Portal;

internal static class PortalJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static JsonElement ParseJson(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json, SerializerOptions);

    public static Guid ReadEventId(AuditEvent auditEvent, JsonElement payload)
    {
        if (TryGetGuid(payload, out var eventId, "eventId"))
        {
            return eventId;
        }

        return auditEvent.Id;
    }

    public static int ReadSchemaVersion(JsonElement payload)
    {
        if (TryGetInt(payload, out var schemaVersion, "schemaVersion"))
        {
            return schemaVersion;
        }

        if (TryGetInt(payload, out schemaVersion, "payload", "schemaVersion"))
        {
            return schemaVersion;
        }

        return 1;
    }

    public static DateTimeOffset ReadTimestamp(AuditEvent auditEvent, JsonElement payload)
    {
        if (TryGetDateTimeOffset(payload, out var timestamp, "timestamp")
            || TryGetDateTimeOffset(payload, out timestamp, "occurredAt")
            || TryGetDateTimeOffset(payload, out timestamp, "payload", "timestamp"))
        {
            return timestamp;
        }

        return auditEvent.CreatedAt;
    }

    public static bool TryReadDeviceId(JsonElement payload, out Guid deviceId) =>
        TryGetGuid(payload, out deviceId, "deviceId")
        || TryGetGuid(payload, out deviceId, "payload", "deviceId")
        || TryGetGuid(payload, out deviceId, "data", "telemetry", "deviceId")
        || TryGetGuid(payload, out deviceId, "data", "summary", "deviceId");

    public static string? TryReadString(JsonElement payload, params string[] path) =>
        TryGetElement(payload, out var element, path) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    public static bool TryGetGuid(JsonElement payload, out Guid value, params string[] path)
    {
        value = Guid.Empty;
        return TryGetElement(payload, out var element, path)
               && element.ValueKind == JsonValueKind.String
               && Guid.TryParse(element.GetString(), out value);
    }

    public static bool TryGetInt(JsonElement payload, out int value, params string[] path)
    {
        value = default;
        return TryGetElement(payload, out var element, path) && element.TryGetInt32(out value);
    }

    public static bool TryGetDateTimeOffset(JsonElement payload, out DateTimeOffset value, params string[] path)
    {
        value = default;
        return TryGetElement(payload, out var element, path)
               && element.ValueKind == JsonValueKind.String
               && DateTimeOffset.TryParse(element.GetString(), out value);
    }

    public static bool TryGetElement(JsonElement payload, out JsonElement element, params string[] path)
    {
        element = payload;
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return false;
            }
        }

        return true;
    }
}
