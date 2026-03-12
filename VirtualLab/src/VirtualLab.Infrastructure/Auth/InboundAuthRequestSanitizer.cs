using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace VirtualLab.Infrastructure.Auth;

public static class InboundAuthRequestSanitizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "proxy-authorization",
        "x-api-key",
        "api-key",
    };

    public static string BuildSafeMetadataJson(
        HttpContext context,
        string targetType,
        string targetKey,
        string authMode,
        string failureReason,
        Guid? callbackTargetId = null,
        Guid? siteId = null,
        Guid? profileId = null)
    {
        Dictionary<string, object?> metadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["targetType"] = targetType,
            ["targetKey"] = targetKey,
            ["authMode"] = authMode,
            ["failureReason"] = failureReason,
            ["method"] = context.Request.Method,
            ["path"] = context.Request.Path.Value,
            ["queryKeys"] = context.Request.Query.Keys.OrderBy(x => x).ToArray(),
            ["headerNames"] = context.Request.Headers.Keys.OrderBy(x => x).ToArray(),
            ["contentType"] = context.Request.ContentType,
            ["contentLength"] = context.Request.ContentLength,
            ["remoteIp"] = context.Connection.RemoteIpAddress?.ToString(),
            ["userAgent"] = context.Request.Headers.UserAgent.ToString(),
            ["siteId"] = siteId,
            ["profileId"] = profileId,
            ["callbackTargetId"] = callbackTargetId,
        };

        return JsonSerializer.Serialize(metadata, JsonOptions);
    }

    public static string SerializeHeaders(IHeaderDictionary headers)
    {
        Dictionary<string, string> sanitized = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in headers)
        {
            sanitized[header.Key] = IsSensitiveHeader(header.Key)
                ? "[REDACTED]"
                : header.Value.ToString();
        }

        return JsonSerializer.Serialize(sanitized, JsonOptions);
    }

    public static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Correlation-Id", out Microsoft.Extensions.Primitives.StringValues value) &&
            !string.IsNullOrWhiteSpace(value.ToString()))
        {
            return value.ToString();
        }

        return context.TraceIdentifier;
    }

    private static bool IsSensitiveHeader(string headerName)
    {
        return SensitiveHeaders.Contains(headerName) ||
               headerName.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
               headerName.Contains("key", StringComparison.OrdinalIgnoreCase) ||
               headerName.Contains("token", StringComparison.OrdinalIgnoreCase);
    }
}
