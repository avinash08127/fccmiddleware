using System.Text;
using FccMiddleware.Adapter.Doms;
using FccMiddleware.Api.Auth;
using FccMiddleware.Api.Infrastructure;
using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Application.Ingestion;
using FccMiddleware.Application.MasterData;
using FccMiddleware.Application.Registration;
using FccMiddleware.Application.Transactions;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.Adapters;
using FccMiddleware.Infrastructure.Deduplication;
using FccMiddleware.Infrastructure.Events;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Repositories;
using FccMiddleware.Infrastructure.Storage;
using FccMiddleware.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using StackExchange.Redis;

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

    // ── Authentication ─────────────────────────────────────────────────────────
    // Two schemes are registered:
    //   1. JwtBearer  — Edge Agent device JWT (scheme: "Bearer")
    //   2. OdooApiKey — Odoo service-to-service API key (scheme: "OdooApiKey")
    //
    // JWT bearer options are configured lazily via AddOptions<JwtBearerOptions>.Configure<IConfiguration>
    // so that IConfiguration is resolved from the DI container at option-creation time, which
    // picks up WebApplicationFactory config overrides in integration tests rather than the
    // eagerly captured builder.Configuration.
    // JwtBearer is the default scheme so that the auth middleware resolves Edge Agent tokens
    // without requiring an explicit scheme name on [Authorize] attributes that don't specify one.
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme)
        .AddScheme<OdooApiKeyAuthOptions, OdooApiKeyAuthHandler>(
            OdooApiKeyAuthOptions.SchemeName, _ => { })
        .AddScheme<DatabricksApiKeyAuthOptions, DatabricksApiKeyAuthHandler>(
            DatabricksApiKeyAuthOptions.SchemeName, _ => { });

    builder.Services
        .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IConfiguration>((options, config) =>
        {
            var jwtSection = config.GetSection(DeviceJwtOptions.SectionName);
            var signingKey = jwtSection["SigningKey"] ?? string.Empty;
            var issuer     = jwtSection["Issuer"]     ?? DeviceJwtOptions.DefaultIssuer;
            var audience   = jwtSection["Audience"]   ?? DeviceJwtOptions.DefaultAudience;

            if (string.IsNullOrEmpty(signingKey))
            {
                // No key configured: JWT bearer is registered but tokens cannot be validated.
                // The upload endpoint will return 401 for all requests.
                return;
            }

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidIssuer              = issuer,
                ValidateAudience         = true,
                ValidAudience            = audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey         = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(signingKey)),
                ValidateLifetime         = true,
                ClockSkew                = TimeSpan.FromSeconds(30)
            };
        });

    builder.Services.AddAuthorization(opts =>
    {
        opts.AddPolicy("EdgeAgentDevice", policy =>
            policy
                .RequireAuthenticatedUser()
                .RequireClaim("site")
                .RequireClaim("lei"));

        opts.AddPolicy(OdooApiKeyAuthOptions.PolicyName, policy =>
            policy
                .AddAuthenticationSchemes(OdooApiKeyAuthOptions.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim("lei"));

        opts.AddPolicy(DatabricksApiKeyAuthOptions.PolicyName, policy =>
            policy
                .AddAuthenticationSchemes(DatabricksApiKeyAuthOptions.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim("role", DatabricksApiKeyAuthOptions.RequiredRole));
    });

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

    builder.Services.AddControllers();

    // ── Infrastructure: Tenant context (populated per-request by auth middleware) ─
    builder.Services.AddScoped<FccMiddleware.Infrastructure.Persistence.TenantContext>();
    builder.Services.AddScoped<FccMiddleware.Domain.Interfaces.ICurrentTenantProvider>(
        sp => sp.GetRequiredService<FccMiddleware.Infrastructure.Persistence.TenantContext>());

    // ── Infrastructure: PostgreSQL (EF Core) ──────────────────────────────────
    builder.Services.AddDbContext<FccMiddlewareDbContext>((sp, opts) =>
        opts.UseNpgsql(
            sp.GetRequiredService<IConfiguration>().GetConnectionString("FccMiddleware")
            ?? string.Empty));

    // Register DbContext as application + dedup + poll interfaces
    builder.Services.AddScoped<IIngestDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IDeduplicationDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IPollTransactionsDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IAcknowledgeTransactionsDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IMasterDataSyncDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IRegistrationDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IAgentConfigDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());

    // ── Infrastructure: Device token service ────────────────────────────────
    builder.Services.AddSingleton<IDeviceTokenService, DeviceTokenService>();

    // ── Infrastructure: Event publisher ───────────────────────────────────────
    builder.Services.AddScoped<IEventPublisher, OutboxEventPublisher>();

    // ── Infrastructure: Redis (StackExchange.Redis) ───────────────────────────
    builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    {
        var connStr = sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis") ?? string.Empty;
        return ConnectionMultiplexer.Connect(connStr);
    });

    // ── Infrastructure: Ingestion services ───────────────────────────────────
    builder.Services.AddScoped<IDeduplicationService, RedisDeduplicationService>();
    builder.Services.AddScoped<ISiteFccConfigProvider, SiteFccConfigProvider>();
    builder.Services.AddSingleton<IRawPayloadArchiver, S3RawPayloadArchiver>();

    // ── Infrastructure: FCC Adapter Factory ──────────────────────────────────
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<IFccAdapterFactory>(sp =>
        FccAdapterFactory.Create(registry =>
        {
            var hcf = sp.GetRequiredService<IHttpClientFactory>();
            registry[FccVendor.DOMS] = cfg =>
            {
                var client = hcf.CreateClient();
                if (!string.IsNullOrEmpty(cfg.HostAddress))
                {
                    client.BaseAddress = new Uri($"http://{cfg.HostAddress}:{cfg.Port}/api/v1/");
                    client.DefaultRequestHeaders.Add("X-API-Key", cfg.ApiKey);
                }
                return new DomsCloudAdapter(client, cfg);
            };
        }));

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
    app.UseAuthentication();
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

    app.MapControllers();

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
