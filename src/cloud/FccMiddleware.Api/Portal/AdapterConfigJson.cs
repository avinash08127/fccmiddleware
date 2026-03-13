using System.Text.Json;
using System.Text.Json.Nodes;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Api.Portal;

internal static class AdapterConfigJson
{
    public static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        var node = JsonNode.Parse(json);
        return node as JsonObject ?? new JsonObject();
    }

    public static string Serialize(JsonObject obj) =>
        obj.ToJsonString(new JsonSerializerOptions(PortalJson.SerializerOptions));

    public static JsonElement ToElement(JsonObject obj) =>
        JsonSerializer.SerializeToElement(obj, PortalJson.SerializerOptions);

    public static JsonObject Clone(JsonObject obj) =>
        (JsonObject)(obj.DeepClone() ?? new JsonObject());

    public static JsonObject Merge(JsonObject baseValues, JsonObject overlay)
    {
        var result = Clone(baseValues);
        foreach (var property in overlay)
        {
            result[property.Key] = property.Value?.DeepClone();
        }

        return result;
    }

    public static JsonObject Pick(JsonObject values, IEnumerable<AdapterFieldDefinition> fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            if (values.TryGetPropertyValue(field.Key, out var value))
            {
                result[field.Key] = value?.DeepClone();
            }
        }

        return result;
    }

    public static JsonObject Diff(JsonObject current, JsonObject baseline, IEnumerable<AdapterFieldDefinition> fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            current.TryGetPropertyValue(field.Key, out var currentValue);
            baseline.TryGetPropertyValue(field.Key, out var baselineValue);

            if (!JsonNodesEqual(currentValue, baselineValue))
            {
                result[field.Key] = currentValue?.DeepClone();
            }
        }

        return result;
    }

    public static JsonObject BuildSecretState(JsonObject values, IEnumerable<AdapterFieldDefinition> fields)
    {
        var result = new JsonObject();
        foreach (var field in fields.Where(item => item.Sensitive))
        {
            values.TryGetPropertyValue(field.Key, out var value);
            result[field.Key] = JsonValue.Create(HasValue(value));
        }

        return result;
    }

    public static JsonObject RedactSecrets(JsonObject values, IEnumerable<AdapterFieldDefinition> fields)
    {
        var result = Clone(values);
        foreach (var field in fields.Where(item => item.Sensitive))
        {
            result.Remove(field.Key);
        }

        return result;
    }

    public static JsonObject BuildFieldSources(
        JsonObject overrideValues,
        IEnumerable<AdapterFieldDefinition> fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            var source = field.Defaultable
                ? (overrideValues.ContainsKey(field.Key) ? "OVERRIDE" : "DEFAULT")
                : "SITE";
            result[field.Key] = JsonValue.Create(source);
        }

        return result;
    }

    public static JsonObject Normalize(
        JsonElement input,
        AdapterCatalogEntry entry,
        bool defaultsOnly)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Adapter config payload must be a JSON object.");
        }

        var allowedFields = entry.Fields
            .Where(field => defaultsOnly ? field.Defaultable : field.SiteConfigurable)
            .ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);

        var result = new JsonObject();
        foreach (var property in input.EnumerateObject())
        {
            if (!allowedFields.TryGetValue(property.Name, out var field))
            {
                continue;
            }

            result[field.Key] = NormalizeValue(field, property.Value);
        }

        ValidateRequiredFields(entry, result, defaultsOnly);
        return result;
    }

    public static JsonObject ReadSiteValues(FccConfig config, AdapterCatalogEntry entry)
    {
        var result = new JsonObject
        {
            ["enabled"] = JsonValue.Create(config.IsActive),
            ["connectionProtocol"] = JsonValue.Create(config.ConnectionProtocol.ToString()),
            ["transactionMode"] = JsonValue.Create(config.IngestionMethod.ToString()),
            ["ingestionMode"] = JsonValue.Create(config.IngestionMode.ToString()),
            ["pullIntervalSeconds"] = config.PullIntervalSeconds is null ? null : JsonValue.Create(config.PullIntervalSeconds.Value),
            ["catchUpPullIntervalSeconds"] = config.CatchUpPullIntervalSeconds is null ? null : JsonValue.Create(config.CatchUpPullIntervalSeconds.Value),
            ["hybridCatchUpIntervalSeconds"] = config.HybridCatchUpIntervalSeconds is null ? null : JsonValue.Create(config.HybridCatchUpIntervalSeconds.Value),
            ["heartbeatIntervalSeconds"] = JsonValue.Create(config.HeartbeatIntervalSeconds),
            ["heartbeatTimeoutSeconds"] = JsonValue.Create(config.HeartbeatTimeoutSeconds),
            ["hostAddress"] = JsonValue.Create(config.HostAddress),
            ["port"] = JsonValue.Create(config.Port),
            ["jplPort"] = config.JplPort is null ? null : JsonValue.Create(config.JplPort.Value),
            ["fcAccessCode"] = config.FcAccessCode is null ? null : JsonValue.Create(config.FcAccessCode),
            ["domsCountryCode"] = config.DomsCountryCode is null ? null : JsonValue.Create(config.DomsCountryCode),
            ["posVersionId"] = config.PosVersionId is null ? null : JsonValue.Create(config.PosVersionId),
            ["configuredPumps"] = config.ConfiguredPumps is null ? null : JsonValue.Create(config.ConfiguredPumps),
            ["dppPorts"] = config.DppPorts is null ? null : JsonValue.Create(config.DppPorts),
            ["reconnectBackoffMaxSeconds"] = config.ReconnectBackoffMaxSeconds is null ? null : JsonValue.Create(config.ReconnectBackoffMaxSeconds.Value),
            ["sharedSecret"] = config.SharedSecret is null ? null : JsonValue.Create(config.SharedSecret),
            ["usnCode"] = config.UsnCode is null ? null : JsonValue.Create(config.UsnCode.Value),
            ["authPort"] = config.AuthPort is null ? null : JsonValue.Create(config.AuthPort.Value),
            ["clientId"] = config.ClientId is null ? null : JsonValue.Create(config.ClientId),
            ["clientSecret"] = config.ClientSecret is null ? null : JsonValue.Create(config.ClientSecret),
            ["webhookSecret"] = config.WebhookSecret is null ? null : JsonValue.Create(config.WebhookSecret),
            ["oauthTokenEndpoint"] = config.OAuthTokenEndpoint is null ? null : JsonValue.Create(config.OAuthTokenEndpoint),
            ["advatecDevicePort"] = config.AdvatecDevicePort is null ? null : JsonValue.Create(config.AdvatecDevicePort.Value),
            ["advatecWebhookToken"] = config.AdvatecWebhookToken is null ? null : JsonValue.Create(config.AdvatecWebhookToken),
            ["advatecEfdSerialNumber"] = config.AdvatecEfdSerialNumber is null ? null : JsonValue.Create(config.AdvatecEfdSerialNumber),
            ["advatecCustIdType"] = config.AdvatecCustIdType is null ? null : JsonValue.Create(config.AdvatecCustIdType.Value),
        };

        result["fccPumpAddressMap"] = ParseEmbeddedJson(config.FccPumpAddressMap);
        result["advatecPumpMap"] = ParseEmbeddedJson(config.AdvatecPumpMap);

        foreach (var field in entry.Fields)
        {
            if (!result.ContainsKey(field.Key) && field.DefaultValue is not null && !field.Defaultable)
            {
                result[field.Key] = field.DefaultValue.DeepClone();
            }
        }

        return result;
    }

    public static JsonObject ReadExtraDefaultsOrOverrides(JsonObject values, AdapterCatalogEntry entry)
    {
        var result = new JsonObject();
        foreach (var field in entry.Fields.Where(field => field.Defaultable && !IsMappedToFccConfig(field.Key)))
        {
            if (values.TryGetPropertyValue(field.Key, out var value))
            {
                result[field.Key] = value?.DeepClone();
            }
        }

        return result;
    }

    public static void ApplyToFccConfig(
        FccConfig config,
        JsonObject effectiveValues,
        AdapterCatalogEntry entry)
    {
        config.ConnectionProtocol = ParseEnum<ConnectionProtocol>(effectiveValues, "connectionProtocol", config.ConnectionProtocol);
        config.IngestionMethod = ParseEnum<IngestionMethod>(effectiveValues, "transactionMode", config.IngestionMethod);
        config.IngestionMode = ParseEnum<IngestionMode>(effectiveValues, "ingestionMode", config.IngestionMode);
        config.IsActive = ParseBool(effectiveValues, "enabled", config.IsActive);
        config.PullIntervalSeconds = ParseNullableInt(effectiveValues, "pullIntervalSeconds", config.PullIntervalSeconds);
        config.CatchUpPullIntervalSeconds = ParseNullableInt(effectiveValues, "catchUpPullIntervalSeconds", config.CatchUpPullIntervalSeconds);
        config.HybridCatchUpIntervalSeconds = ParseNullableInt(effectiveValues, "hybridCatchUpIntervalSeconds", config.HybridCatchUpIntervalSeconds);
        config.HeartbeatIntervalSeconds = ParseInt(effectiveValues, "heartbeatIntervalSeconds", config.HeartbeatIntervalSeconds);
        config.HeartbeatTimeoutSeconds = ParseInt(effectiveValues, "heartbeatTimeoutSeconds", config.HeartbeatTimeoutSeconds);
        config.HostAddress = ParseString(effectiveValues, "hostAddress", config.HostAddress) ?? config.HostAddress;
        config.Port = ParseInt(effectiveValues, "port", config.Port);
        config.JplPort = ParseNullableInt(effectiveValues, "jplPort", config.JplPort);
        config.FcAccessCode = ParseString(effectiveValues, "fcAccessCode", config.FcAccessCode);
        config.DomsCountryCode = ParseString(effectiveValues, "domsCountryCode", config.DomsCountryCode);
        config.PosVersionId = ParseString(effectiveValues, "posVersionId", config.PosVersionId);
        config.ConfiguredPumps = ParseString(effectiveValues, "configuredPumps", config.ConfiguredPumps);
        config.DppPorts = ParseString(effectiveValues, "dppPorts", config.DppPorts);
        config.ReconnectBackoffMaxSeconds = ParseNullableInt(effectiveValues, "reconnectBackoffMaxSeconds", config.ReconnectBackoffMaxSeconds);
        config.SharedSecret = ParseString(effectiveValues, "sharedSecret", config.SharedSecret);
        config.UsnCode = ParseNullableInt(effectiveValues, "usnCode", config.UsnCode);
        config.AuthPort = ParseNullableInt(effectiveValues, "authPort", config.AuthPort);
        config.FccPumpAddressMap = SerializeEmbeddedJson(effectiveValues, "fccPumpAddressMap", config.FccPumpAddressMap);
        config.ClientId = ParseString(effectiveValues, "clientId", config.ClientId);
        config.ClientSecret = ParseString(effectiveValues, "clientSecret", config.ClientSecret);
        config.WebhookSecret = ParseString(effectiveValues, "webhookSecret", config.WebhookSecret);
        config.OAuthTokenEndpoint = ParseString(effectiveValues, "oauthTokenEndpoint", config.OAuthTokenEndpoint);
        config.AdvatecDevicePort = ParseNullableInt(effectiveValues, "advatecDevicePort", config.AdvatecDevicePort);
        config.AdvatecWebhookToken = ParseString(effectiveValues, "advatecWebhookToken", config.AdvatecWebhookToken);
        config.AdvatecEfdSerialNumber = ParseString(effectiveValues, "advatecEfdSerialNumber", config.AdvatecEfdSerialNumber);
        config.AdvatecCustIdType = ParseNullableInt(effectiveValues, "advatecCustIdType", config.AdvatecCustIdType);
        config.AdvatecPumpMap = SerializeEmbeddedJson(effectiveValues, "advatecPumpMap", config.AdvatecPumpMap);

        if (!string.IsNullOrWhiteSpace(config.WebhookSecret))
        {
            config.WebhookSecretHash = ComputeSha256Hex(config.WebhookSecret);
        }

        if (!string.IsNullOrWhiteSpace(config.AdvatecWebhookToken))
        {
            config.AdvatecWebhookTokenHash = ComputeSha256Hex(config.AdvatecWebhookToken);
        }

        config.UpdatedAt = DateTimeOffset.UtcNow;
        config.ConfigVersion += 1;
    }

    public static JsonObject BuildAuditDiff(
        JsonObject beforeValues,
        JsonObject afterValues,
        IEnumerable<AdapterFieldDefinition> fields)
    {
        var result = new JsonObject();

        foreach (var field in fields)
        {
            beforeValues.TryGetPropertyValue(field.Key, out var beforeValue);
            afterValues.TryGetPropertyValue(field.Key, out var afterValue);

            if (JsonNodesEqual(beforeValue, afterValue))
            {
                continue;
            }

            result[field.Key] = new JsonObject
            {
                ["before"] = field.Sensitive ? JsonValue.Create(beforeValue is null ? null : "***redacted***") : beforeValue?.DeepClone(),
                ["after"] = field.Sensitive ? JsonValue.Create(afterValue is null ? null : "***redacted***") : afterValue?.DeepClone()
            };
        }

        return result;
    }

    public static int ReadPumpNumberOffset(JsonObject effectiveValues) =>
        ParseInt(effectiveValues, "pumpNumberOffset", 0);

    public static IReadOnlyDictionary<string, string> ReadProductCodeMapping(JsonObject effectiveValues)
    {
        if (!effectiveValues.TryGetPropertyValue("productCodeMapping", out var value) || value is not JsonObject mapping)
        {
            return new Dictionary<string, string>();
        }

        return mapping
            .Where(item => item.Value is JsonValue)
            .ToDictionary(
                item => item.Key,
                item => item.Value?.GetValue<string>() ?? item.Key,
                StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateRequiredFields(AdapterCatalogEntry entry, JsonObject values, bool defaultsOnly)
    {
        foreach (var field in entry.Fields.Where(field => field.Required && (defaultsOnly ? field.Defaultable : field.SiteConfigurable)))
        {
            if (!values.TryGetPropertyValue(field.Key, out var value) || !HasValue(value))
            {
                if (defaultsOnly)
                {
                    continue;
                }

                throw new InvalidOperationException($"Field '{field.Key}' is required.");
            }
        }
    }

    private static JsonNode? NormalizeValue(AdapterFieldDefinition field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return field.Type switch
        {
            "boolean" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => JsonValue.Create(value.GetBoolean()),
            "number" when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) => JsonValue.Create(i),
            "text" or "secret" when value.ValueKind == JsonValueKind.String => NormalizeString(field, value.GetString()),
            "select" when value.ValueKind == JsonValueKind.String => NormalizeSelect(field, value.GetString()),
            "json" when value.ValueKind == JsonValueKind.Object => JsonNode.Parse(value.GetRawText()),
            _ => throw new InvalidOperationException($"Field '{field.Key}' has invalid value type.")
        };
    }

    private static JsonNode? NormalizeString(AdapterFieldDefinition field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return field.Required ? throw new InvalidOperationException($"Field '{field.Key}' is required.") : null;
        }

        return JsonValue.Create(value.Trim());
    }

    private static JsonNode NormalizeSelect(AdapterFieldDefinition field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (field.Required)
            {
                throw new InvalidOperationException($"Field '{field.Key}' is required.");
            }

            return null!;
        }

        if (field.Options is not null && !field.Options.Any(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Field '{field.Key}' has unsupported option '{value}'.");
        }

        return JsonValue.Create(value);
    }

    private static bool JsonNodesEqual(JsonNode? left, JsonNode? right) =>
        string.Equals(left?.ToJsonString(), right?.ToJsonString(), StringComparison.Ordinal);

    private static bool HasValue(JsonNode? value) =>
        value switch
        {
            null => false,
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var str) => !string.IsNullOrWhiteSpace(str),
            _ => true
        };

    private static bool ParseBool(JsonObject values, string key, bool fallback) =>
        values.TryGetPropertyValue(key, out var value) && value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var result)
            ? result
            : fallback;

    private static int ParseInt(JsonObject values, string key, int fallback) =>
        values.TryGetPropertyValue(key, out var value) && value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var result)
            ? result
            : fallback;

    private static int? ParseNullableInt(JsonObject values, string key, int? fallback)
    {
        if (!values.TryGetPropertyValue(key, out var value))
        {
            return fallback;
        }

        if (value is null)
        {
            return null;
        }

        return value is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var result)
            ? result
            : fallback;
    }

    private static string? ParseString(JsonObject values, string key, string? fallback)
    {
        if (!values.TryGetPropertyValue(key, out var value))
        {
            return fallback;
        }

        if (value is null)
        {
            return null;
        }

        return value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var result)
            ? result
            : fallback;
    }

    private static TEnum ParseEnum<TEnum>(JsonObject values, string key, TEnum fallback)
        where TEnum : struct, Enum
    {
        var value = ParseString(values, key, null);
        return value is not null && Enum.TryParse<TEnum>(value, true, out var parsed)
            ? parsed
            : fallback;
    }

    private static JsonNode? ParseEmbeddedJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json);

    private static string? SerializeEmbeddedJson(JsonObject values, string key, string? fallback)
    {
        if (!values.TryGetPropertyValue(key, out var value))
        {
            return fallback;
        }

        return value?.ToJsonString();
    }

    private static string ComputeSha256Hex(string input)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsMappedToFccConfig(string key) =>
        key is not ("pumpNumberOffset" or "productCodeMapping");
}
