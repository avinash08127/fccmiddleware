using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace FccMiddleware.Api.Infrastructure;

/// <summary>
/// Writes health check results as structured JSON matching the HealthResponse schema in cloud-api.yaml.
/// Shape: { status, timestamp, version, dependencies: { name: { status, latencyMs, message } } }
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

    public static Task WriteResponse(HttpContext context, HealthReport report)
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
                    latencyMs = (long)Math.Round(e.Value.Duration.TotalMilliseconds),
                    message = e.Value.Description
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
