using FccMiddleware.Api.Infrastructure;
using FccMiddleware.ServiceDefaults;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

// Bootstrap logger — active only until DI container is built.
// ServiceDefaults replaces this with the full structured-JSON logger.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Registers: Serilog (structured JSON → console), OpenTelemetry, base health check
    builder.AddServiceDefaults();

    builder.Services.AddAuthorization();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
        {
            Title = "FCC Middleware API",
            Version = "v1"
        });

        // JWT bearer auth button in Swagger UI
        c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Azure Entra JWT bearer token"
        });

        // Include XML doc comments
        var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
            c.IncludeXmlComments(xmlPath);
    });

    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<Program>();
        cfg.RegisterServicesFromAssembly(typeof(FccMiddleware.Application.Common.Result<>).Assembly);
    });

    // Health checks: PostgreSQL + Redis (registered here; liveness stub registered in ServiceDefaults)
    // Use factory overloads so connection strings are resolved lazily at health-check execution
    // time — this is necessary for WebApplicationFactory overrides to take effect in tests.
    builder.Services.AddHealthChecks()
        .AddNpgSql(
            sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("FccMiddleware") ?? string.Empty,
            name: "postgres", tags: ["ready"])
        .AddRedis(
            sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis") ?? string.Empty,
            name: "redis", tags: ["ready"]);

    var app = builder.Build();

    // Emit one structured-JSON log line per HTTP request
    app.UseSerilogRequestLogging();

    var swaggerEnabled = builder.Configuration.GetValue<bool?>("Swagger:Enabled")
                         ?? app.Environment.IsDevelopment();
    if (swaggerEnabled)
    {
        app.UseSwagger();
        app.UseSwaggerUI();

        // Redirect root → Swagger UI for convenience in dev/staging
        app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
    }

    app.UseHttpsRedirection();
    app.UseAuthorization();

    // /health       → liveness  (is the process up?)
    // /health/ready → readiness (are DB + Redis reachable?)
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        Predicate      = check => check.Tags.Contains("live"),
        ResponseWriter = HealthResponseWriter.WriteResponse
    });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate      = check => check.Tags.Contains("ready"),
        ResponseWriter = HealthResponseWriter.WriteResponse
    });

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { } // exposed for WebApplicationFactory in integration tests
