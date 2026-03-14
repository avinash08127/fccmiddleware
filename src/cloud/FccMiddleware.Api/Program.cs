using System.Text;
using System.Threading.RateLimiting;
using FccMiddleware.Api.AgentControl;
using FccMiddleware.Api.Auth;
using FccMiddleware.Api.Infrastructure;
using FccMiddleware.Api.Portal;
using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Application.Ingestion;
using FccMiddleware.Application.MasterData;
using FccMiddleware.Application.PreAuth;
using FccMiddleware.Application.Reconciliation;
using FccMiddleware.Application.Registration;
using FccMiddleware.Application.DiagnosticLogs;
using FccMiddleware.Application.Telemetry;
using FccMiddleware.Application.Transactions;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.Adapters;
using FccMiddleware.Application.DeadLetter;
using FccMiddleware.Infrastructure.DeadLetter;
using FccMiddleware.Infrastructure.Deduplication;
using FccMiddleware.Infrastructure.Security;
using FccMiddleware.Infrastructure.Events;
using FccMiddleware.Infrastructure.Observability;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Repositories;
using FccMiddleware.Infrastructure.Storage;
using FccMiddleware.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using StackExchange.Redis;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Application.Observability;
using Microsoft.Extensions.Options;

// Bootstrap logger — active only until DI container is built.
// ServiceDefaults replaces this with the full structured-JSON logger.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // S-3: Lower default max request body size from 30 MB to 5 MB.
    // Per-endpoint overrides via [RequestSizeLimit] tighten further for webhooks.
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = 5 * 1024 * 1024; // 5 MB
    });

    // Registers: Serilog (structured JSON → console), OpenTelemetry, base health check
    builder.AddServiceDefaults();

    // CORS — allow Angular dev server in Development
    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("DevCors", policy =>
                policy.WithOrigins("http://localhost:4200")
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials());
        });
    }

    // ── Authentication ─────────────────────────────────────────────────────────
    // Auth schemes:
    //   1. Bearer       — Edge Agent device JWT
    //   2. PortalBearer — Azure Entra / MSAL JWT for portal users
    //   3. FccHmac      — FCC service-to-service HMAC auth
    //   4. OdooApiKey   — Odoo service-to-service API key
    //   5. DatabricksApiKey — Databricks service-to-service API key
    //
    // JWT bearer options are configured lazily via AddOptions<JwtBearerOptions>.Configure<IConfiguration>
    // so that IConfiguration is resolved from the DI container at option-creation time, which
    // picks up WebApplicationFactory config overrides in integration tests rather than the
    // eagerly captured builder.Configuration.
    // JwtBearer is the default scheme so that the auth middleware resolves Edge Agent tokens
    // without requiring an explicit scheme name on [Authorize] attributes that don't specify one.
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(PortalJwtOptions.SchemeName)
        .AddScheme<FccHmacAuthOptions, FccHmacAuthHandler>(
            FccHmacAuthOptions.SchemeName, _ => { })
        .AddScheme<OdooApiKeyAuthOptions, OdooApiKeyAuthHandler>(
            OdooApiKeyAuthOptions.SchemeName, _ => { })
        .AddScheme<DatabricksApiKeyAuthOptions, DatabricksApiKeyAuthHandler>(
            DatabricksApiKeyAuthOptions.SchemeName, _ => { });

    builder.Services.Configure<FccHmacAuthOptions>(
        builder.Configuration.GetSection(FccHmacAuthOptions.SectionName));

    builder.Services
        .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IConfiguration>((options, config) =>
        {
            options.MapInboundClaims = false;

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
                ClockSkew                = TimeSpan.FromSeconds(30),
                RoleClaimType            = "roles",
                NameClaimType            = "oid"
            };
        });

    builder.Services
        .AddOptions<JwtBearerOptions>(PortalJwtOptions.SchemeName)
        .Configure<IConfiguration>((options, config) =>
        {
            options.MapInboundClaims = false;

            var portalSection = config.GetSection(PortalJwtOptions.SectionName);
            var signingKey = portalSection["SigningKey"] ?? string.Empty;
            var authority = portalSection["Authority"] ?? string.Empty;
            var audience = portalSection["Audience"] ?? string.Empty;
            var clientId = portalSection["ClientId"] ?? string.Empty;
            var additionalAudiences = portalSection
                .GetSection("AdditionalAudiences")
                .GetChildren()
                .Select(section => section.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray();

            var validAudiences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(audience))
            {
                validAudiences.Add(audience);
            }

            if (!string.IsNullOrWhiteSpace(clientId))
            {
                validAudiences.Add(clientId);
                validAudiences.Add($"api://{clientId}");
            }

            foreach (var additionalAudience in additionalAudiences)
            {
                validAudiences.Add(additionalAudience);
            }

            // Entra may issue v1.0 tokens (sts.windows.net issuer) or v2.0 tokens
            // (login.microsoftonline.com issuer) depending on the app manifest's
            // accessTokenAcceptedVersion. Accept both so either version works.
            var tenantId = authority
                .Replace("https://login.microsoftonline.com/", "")
                .TrimEnd('/').Replace("/v2.0", "");
            var validIssuers = new[]
            {
                $"https://login.microsoftonline.com/{tenantId}/v2.0",
                $"https://sts.windows.net/{tenantId}/",
            };

            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = validIssuers,
                ValidateAudience = true,
                ValidAudiences = validAudiences,
                ValidateIssuerSigningKey = !string.IsNullOrWhiteSpace(signingKey),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                RoleClaimType = "roles",
                NameClaimType = "oid"
            };

            // Log JWT validation failures so we can diagnose 401s
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("PortalJwt");
                    logger.LogWarning(context.Exception,
                        "PortalBearer token validation failed: {Message}", context.Exception.Message);
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var logger = context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("PortalJwt");
                    var aud = context.SecurityToken is Microsoft.IdentityModel.JsonWebTokens.JsonWebToken jwt
                        ? string.Join(", ", jwt.Audiences) : "unknown";
                    logger.LogInformation("PortalBearer token validated. Audiences: {Audiences}", aud);
                    return Task.CompletedTask;
                },
            };

            if (!string.IsNullOrWhiteSpace(signingKey))
            {
                options.TokenValidationParameters.IssuerSigningKey =
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
                return;
            }

            if (string.IsNullOrWhiteSpace(authority))
            {
                return;
            }

            options.Authority = authority.TrimEnd('/');
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        });

    builder.Services.AddScoped<PortalUserService>();
    builder.Services.AddSingleton<AdapterCatalogService>();
    builder.Services.AddScoped<AdapterConfigPortalService>();

    builder.Services.AddAuthorization(opts =>
    {
        static bool HasAnyRole(AuthorizationHandlerContext context, params string[] allowedRoles)
        {
            var roles = context.User.FindAll("roles")
                .SelectMany(claim => claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return roles.Overlaps(allowedRoles);
        }

        // Portal policies — role names are injected by PortalRoleEnrichmentMiddleware
        // from the local portal_users table (Entra tokens do not carry roles).
        opts.AddPolicy("PortalUser", policy =>
            policy
                .AddAuthenticationSchemes(PortalJwtOptions.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasAnyRole(context,
                    "FccAdmin", "FccUser", "FccViewer")));

        opts.AddPolicy("PortalReconciliationReview", policy =>
            policy
                .AddAuthenticationSchemes(PortalJwtOptions.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasAnyRole(context,
                    "FccAdmin", "FccUser")));

        opts.AddPolicy("PortalAdminWrite", policy =>
            policy
                .AddAuthenticationSchemes(PortalJwtOptions.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasAnyRole(context,
                    "FccAdmin", "FccUser")));

        opts.AddPolicy("PortalUserManagement", policy =>
            policy
                .AddAuthenticationSchemes(PortalJwtOptions.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => HasAnyRole(context,
                    "FccAdmin")));

        // H-13: Pin to JWT Bearer scheme so HMAC-authenticated clients with
        // matching "site"/"lei" claims cannot satisfy this policy.
        opts.AddPolicy("EdgeAgentDevice", policy =>
            policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim("site")
                .RequireClaim("lei"));

        opts.AddPolicy(FccHmacAuthOptions.PolicyName, policy =>
            policy
                .AddAuthenticationSchemes(FccHmacAuthOptions.SchemeName)
                .RequireAuthenticatedUser());

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
            Description = "JWT bearer token. Portal endpoints expect Azure Entra access tokens; device endpoints expect device JWTs."
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

    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

    // S-3: Per-IP rate limiting for anonymous and secret-authenticated routes.
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Anonymous webhook ingestion endpoints (Radix, Petronite, Advatec)
        options.AddPolicy("anonymous-ingress", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                }));

        // Device registration (bootstrap-token authenticated)
        options.AddPolicy("registration", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                }));

        // Token refresh endpoint
        options.AddPolicy("token-refresh", httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                }));
    });

    builder.Services.Configure<ApiBehaviorOptions>(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var details = context.ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value!.Errors.Select(error =>
                        string.IsNullOrWhiteSpace(error.ErrorMessage) ? "The input was not valid." : error.ErrorMessage).ToArray());

            return new BadRequestObjectResult(new FccMiddleware.Contracts.Common.ErrorResponse
            {
                ErrorCode = "VALIDATION.INVALID_PAYLOAD",
                Message = "Request payload failed validation.",
                Details = details,
                TraceId = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier,
                Timestamp = DateTimeOffset.UtcNow.ToString("O"),
                Retryable = false
            });
        };
    });

    // ── Infrastructure: Tenant context (populated per-request by auth middleware) ─
    builder.Services.AddScoped<FccMiddleware.Infrastructure.Persistence.TenantContext>();
    builder.Services.AddScoped<FccMiddleware.Domain.Interfaces.ICurrentTenantProvider>(
        sp => sp.GetRequiredService<FccMiddleware.Infrastructure.Persistence.TenantContext>());
    builder.Services.AddScoped<PortalAccessResolver>();
    builder.Services.Configure<AgentCommandsOptions>(
        builder.Configuration.GetSection(AgentCommandsOptions.SectionName));
    builder.Services.Configure<BootstrapTokensOptions>(
        builder.Configuration.GetSection(BootstrapTokensOptions.SectionName));
    builder.Services.Configure<SuspiciousDeviceWorkflowOptions>(
        builder.Configuration.GetSection(SuspiciousDeviceWorkflowOptions.SectionName));
    builder.Services.Configure<FirebaseMessagingOptions>(
        builder.Configuration.GetSection(FirebaseMessagingOptions.SectionName));
    builder.Services.Configure<FccMiddleware.Application.Notifications.EmailNotificationOptions>(
        builder.Configuration.GetSection(FccMiddleware.Application.Notifications.EmailNotificationOptions.SectionName));

    // ── Infrastructure: Email notifications (bootstrap token generation) ─────
    builder.Services.AddSingleton<FccMiddleware.Application.Notifications.IEmailNotificationService>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<FccMiddleware.Application.Notifications.EmailNotificationOptions>>().Value;
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        if (!opts.Enabled)
            return new FccMiddleware.Infrastructure.Messaging.NoOpEmailService(
                loggerFactory.CreateLogger<FccMiddleware.Infrastructure.Messaging.NoOpEmailService>());

        return opts.Provider?.ToUpperInvariant() switch
        {
            "AWSSES" => new FccMiddleware.Infrastructure.Messaging.AwsSesEmailService(
                new Amazon.SimpleEmailV2.AmazonSimpleEmailServiceV2Client(
                    Amazon.RegionEndpoint.GetBySystemName(opts.AwsSesRegion)),
                sp.GetRequiredService<IOptions<FccMiddleware.Application.Notifications.EmailNotificationOptions>>(),
                loggerFactory.CreateLogger<FccMiddleware.Infrastructure.Messaging.AwsSesEmailService>()),
            "SENDGRID" => new FccMiddleware.Infrastructure.Messaging.SendGridEmailService(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("SendGrid"),
                sp.GetRequiredService<IOptions<FccMiddleware.Application.Notifications.EmailNotificationOptions>>(),
                loggerFactory.CreateLogger<FccMiddleware.Infrastructure.Messaging.SendGridEmailService>()),
            _ => new FccMiddleware.Infrastructure.Messaging.NoOpEmailService(
                loggerFactory.CreateLogger<FccMiddleware.Infrastructure.Messaging.NoOpEmailService>())
        };
    });

    // ── Infrastructure: Field-level encryption for database secrets ──────────
    builder.Services.AddSingleton<FccMiddleware.Application.Common.IFieldEncryptor>(sp =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var hexKey = config["FieldEncryption:Key"]
            ?? throw new InvalidOperationException(
                "FieldEncryption:Key is required. Set a 64-character hex string (32 bytes) in configuration or environment.");
        var keyBytes = Convert.FromHexString(hexKey);
        return new FccMiddleware.Infrastructure.Security.AesGcmFieldEncryptor(
            keyBytes,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<FccMiddleware.Infrastructure.Security.AesGcmFieldEncryptor>());
    });

    // ── Infrastructure: In-memory cache (OB-P01: site config response caching) ─
    builder.Services.AddMemoryCache();

    // ── Infrastructure: PostgreSQL (EF Core) ──────────────────────────────────
    builder.Services.AddDbContext<FccMiddlewareDbContext>((sp, opts) =>
        opts.UseNpgsql(
            sp.GetRequiredService<IConfiguration>().GetConnectionString("FccMiddleware")
            ?? string.Empty));

    // Factory for creating short-lived contexts used in parallel query scenarios
    builder.Services.AddDbContextFactory<FccMiddlewareDbContext>((sp, opts) =>
        opts.UseNpgsql(
            sp.GetRequiredService<IConfiguration>().GetConnectionString("FccMiddleware")
            ?? string.Empty), ServiceLifetime.Scoped);

    // Register DbContext as application + dedup + poll interfaces
    builder.Services.AddScoped<IIngestDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IDeduplicationDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IPollTransactionsDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IAcknowledgeTransactionsDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IMasterDataSyncDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IRegistrationDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IAgentConfigDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<ITelemetryDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IDiagnosticLogsDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IPreAuthDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddScoped<IReconciliationDbContext>(sp => sp.GetRequiredService<FccMiddlewareDbContext>());
    builder.Services.AddSingleton<IObservabilityMetrics, CloudWatchEmfMetricSink>();
    builder.Services.AddHttpClient<IAgentPushHintSender, FirebasePushHintSender>();
    builder.Services.AddScoped<IAgentPushHintDispatcher, AgentPushHintDispatcher>();
    builder.Services.Configure<ReconciliationOptions>(
        builder.Configuration.GetSection(ReconciliationOptions.SectionName));
    builder.Services.AddScoped<ReconciliationMatchingService>();

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

    // ── Infrastructure: Dead-letter services ─────────────────────────────────
    builder.Services.AddScoped<IDeadLetterService, DeadLetterService>();
    builder.Services.AddScoped<IDlqReplayService, DlqReplayService>();

    // ── Infrastructure: Registration throttle (FM-S02: IP-based brute-force protection) ──
    builder.Services.AddSingleton<IRegistrationThrottleService, RedisRegistrationThrottleService>();

    // ── Infrastructure: Ingestion services ───────────────────────────────────
    builder.Services.AddScoped<IDeduplicationService, RedisDeduplicationService>();
    builder.Services.AddScoped<ISiteFccConfigProvider, SiteFccConfigProvider>();
    builder.Services.AddSingleton<IRawPayloadArchiver, S3RawPayloadArchiver>();

    // ── Infrastructure: FCC Adapter Factory ──────────────────────────────────
    builder.Services.AddCloudFccAdapterFactory();

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
    SecurityConfigurationValidator.Validate(app.Configuration, app.Environment);

    // Emit one structured-JSON log line per HTTP request
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseMiddleware<GlobalExceptionHandlerMiddleware>();
    app.UseSerilogRequestLogging(opts =>
    {
        opts.EnrichDiagnosticContext = (diag, httpContext) =>
        {
            var correlationId = CorrelationIdMiddleware.GetCorrelationId(httpContext);
            if (correlationId != Guid.Empty)
            {
                diag.Set("correlationId", correlationId);
            }

            diag.Set("httpMethod", httpContext.Request.Method);
            diag.Set("httpPath", httpContext.Request.Path.Value ?? "/");
            diag.Set("eventType", "http_request");
        };
    });

    // I-1: Swagger:Enabled must be false in production configuration.
    // When true, Swagger UI is accessible to any authenticated portal user,
    // exposing the full API surface. Only enable in dev/staging environments.
    var swaggerEnabled = builder.Configuration.GetValue<bool?>("Swagger:Enabled")
                         ?? app.Environment.IsDevelopment();
    if (swaggerEnabled)
    {
        app.UseSwagger();
        app.UseSwaggerUI();

        // Redirect root → Swagger UI for convenience in dev/staging
        app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
        app.UseHsts();
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseCors("DevCors");
    }

    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseMiddleware<PortalRoleEnrichmentMiddleware>();
    app.UseMiddleware<DeviceActiveCheckMiddleware>();
    app.UseMiddleware<TenantScopeMiddleware>();
    app.UseAuthorization();

    // /health       → liveness  (is the process up?) — minimal, unauthenticated
    // /health/ready → readiness (are DB + Redis reachable?) — detailed, authenticated
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        Predicate      = check => check.Tags.Contains("live"),
        ResponseWriter = HealthResponseWriter.WriteMinimalResponse
    });
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate      = check => check.Tags.Contains("ready"),
        ResponseWriter = HealthResponseWriter.WriteDetailedResponse
    }).RequireAuthorization("PortalUser");

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
