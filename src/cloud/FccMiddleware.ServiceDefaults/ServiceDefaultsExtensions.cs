using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using FccMiddleware.ServiceDefaults.Logging;
using FccMiddleware.ServiceDefaults.Security;
using Serilog;
using Serilog.Formatting.Compact;

namespace FccMiddleware.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    /// <summary>
    /// Registers cross-cutting defaults: structured JSON logging (Serilog) and base health check.
    /// Call this once in each host's Program.cs before host-specific registrations.
    /// TODO CB-ServiceDefaults: Add OpenTelemetry (tracing + metrics) once packages confirmed.
    /// </summary>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.AddDefaultSerilog();
        builder.AddDefaultHealthChecks();
        return builder;
    }

    public static IHostApplicationBuilder AddDefaultSerilog(this IHostApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, cfg) => cfg
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Destructure.With<RedactingDestructuringPolicy>()
            .Enrich.With<ActivityEnricher>()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", builder.Environment.ApplicationName)
            .Enrich.WithProperty("environment", builder.Environment.EnvironmentName)
            // Structured JSON to console (CLEF format — parseable by Seq, CloudWatch Logs Insights, etc.)
            .WriteTo.Console(new RenderedCompactJsonFormatter()));

        return builder;
    }

    private static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return builder;
    }
}
