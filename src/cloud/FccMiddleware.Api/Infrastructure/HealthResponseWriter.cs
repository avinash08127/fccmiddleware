using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace FccMiddleware.Api.Infrastructure;

/// <summary>
/// Writes health check results as structured JSON matching the HealthResponse schema in cloud-api.yaml.
/// Two response modes:
///   - Minimal (unauthenticated /health): { status }
///   - Detailed (authenticated /health/ready): { status, timestamp, version, dependencies }
/// </summary>
internal static class HealthResponseWriter
{
    private static readonly string _version =
        System.Reflection.Assembly.GetEntryAssembly()
            ?.GetName().Version?.ToString(3) ?? "unknown";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Minimal liveness response — returns only aggregate status.
    /// Safe for unauthenticated callers; does not disclose version, dependency names, or messages.
    /// </summary>
    public static Task WriteMinimalResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var body = new { status = MapStatus(report.Status) };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body, _jsonOptions));
    }

    /// <summary>
    /// Detailed readiness response — includes version and dependency breakdown.
    /// Should only be served to authenticated/internal callers.
    /// </summary>
    public static Task WriteDetailedResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var body = new
        {
            status = MapStatus(report.Status),
            timestamp = DateTimeOffset.UtcNow,
            version = _version,
            dependencies = report.Entries.ToDictionary(
                e => e.Key,
                e => (object)new
                {
                    status = MapStatus(e.Value.Status),
                    latencyMs = (long)Math.Round(e.Value.Duration.TotalMilliseconds)
                })
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body, _jsonOptions));
    }

    private static string MapStatus(HealthStatus status) => status switch
    {
        HealthStatus.Healthy  => "HEALTHY",
        HealthStatus.Degraded => "DEGRADED",
        _                     => "UNHEALTHY"
    };
}
