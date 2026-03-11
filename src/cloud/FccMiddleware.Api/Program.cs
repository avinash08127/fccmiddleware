using FccMiddleware.ServiceDefaults;
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

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "FCC Middleware API",
            Version = "v1"
        });
        c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Azure Entra JWT bearer token"
        });
    });

    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssemblyContaining<Program>();
        cfg.RegisterServicesFromAssembly(typeof(FccMiddleware.Application.Common.Result<>).Assembly);
    });

    // Health checks: PostgreSQL + Redis (registered here; liveness stub registered in ServiceDefaults)
    builder.Services.AddHealthChecks()
        .AddNpgSql(builder.Configuration.GetConnectionString("FccMiddleware")!,
            name: "postgres", tags: ["ready"])
        .AddRedis(builder.Configuration.GetConnectionString("Redis")!,
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
    }

    app.UseHttpsRedirection();
    app.UseAuthorization();
    app.MapControllers();

    // /health       → liveness  (is the process up?)
    // /health/ready → readiness (are DB + Redis reachable?)
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("live")
    });
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    });

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { } // exposed for WebApplicationFactory in integration tests
