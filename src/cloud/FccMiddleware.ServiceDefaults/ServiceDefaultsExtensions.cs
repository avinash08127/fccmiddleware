using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using FccMiddleware.ServiceDefaults.Logging;
using FccMiddleware.ServiceDefaults.Security;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace FccMiddleware.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    /// <summary>
    /// Registers cross-cutting defaults: structured JSON logging (Serilog),
    /// OpenTelemetry (tracing + metrics), and base health check.
    /// Call this once in each host's Program.cs before host-specific registrations.
    /// </summary>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.AddDefaultSerilog();
        builder.AddDefaultOpenTelemetry();
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

    private static IHostApplicationBuilder AddDefaultOpenTelemetry(this IHostApplicationBuilder builder)
    {
        var serviceName = builder.Environment.ApplicationName;
        var serviceVersion = typeof(ServiceDefaultsExtensions).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: serviceName,
                    serviceVersion: serviceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = builder.Environment.EnvironmentName
                }))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(opts =>
                {
                    // Skip health check endpoints to reduce noise
                    opts.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health");
                })
                .AddHttpClientInstrumentation()
                .AddSource(serviceName)
                .SetupExporter(builder))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(serviceName)
                .SetupExporter(builder));

        return builder;
    }

    private static TracerProviderBuilder SetupExporter(
        this TracerProviderBuilder tracing, IHostApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(opts => opts.Endpoint = new Uri(otlpEndpoint));
        }

        return tracing;
    }

    private static MeterProviderBuilder SetupExporter(
        this MeterProviderBuilder metrics, IHostApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            metrics.AddOtlpExporter(opts => opts.Endpoint = new Uri(otlpEndpoint));
        }

        return metrics;
    }

    private static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return builder;
    }
}
