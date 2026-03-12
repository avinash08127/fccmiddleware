using FccDesktopAgent.Api.Auth;
using FccDesktopAgent.Api.Endpoints;
using FccDesktopAgent.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FccDesktopAgent.Api;

/// <summary>
/// Extension methods that register and configure the embedded Kestrel local REST API.
/// Call <see cref="AddAgentApi"/> on the service collection, then <see cref="MapLocalApi"/>
/// on the built <see cref="WebApplication"/>. Both Program.cs entry points (GUI + headless
/// service) use these to keep the two host modes in sync.
/// </summary>
public static class LocalApiStartup
{
    /// <summary>
    /// Registers local API services: JSON options, API key options from the "LocalApi"
    /// configuration section. Call before <c>builder.Build()</c>.
    /// </summary>
    public static IServiceCollection AddAgentApi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind LocalApiOptions from "LocalApi" section (port + api key)
        services.Configure<LocalApiOptions>(configuration.GetSection(LocalApiOptions.SectionName));

        // Load LAN API key from credential store on startup.
        // The key is persisted during provisioning (ProvisioningWindow) so that
        // ApiKeyMiddleware has a valid key after restart.
        services.AddSingleton<IPostConfigureOptions<LocalApiOptions>, CredentialStoreApiKeyPostConfigure>();

        // Configure System.Text.Json for all Minimal API responses:
        //   - camelCase field names (matches OpenAPI spec conventions)
        //   - omit null fields (cleaner wire format)
        //   - write enums as strings
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        });

        return services;
    }

    /// <summary>
    /// Wires up exception handling middleware, API key authentication, and all 8 Minimal API
    /// endpoint groups. Call after <c>builder.Build()</c>.
    /// </summary>
    public static WebApplication MapLocalApi(this WebApplication app)
    {
        // ── Exception handler (outermost — catches errors from all downstream middleware) ──
        app.UseExceptionHandler(errorApp =>
            errorApp.Run(async context =>
            {
                var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("FccDesktopAgent.Api.LocalApiStartup");
                logger.LogError(exceptionFeature?.Error, "Unhandled exception on {Method} {Path}",
                    context.Request.Method, context.Request.Path);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    errorCode = "INTERNAL_ERROR",
                    message = "An unexpected agent error occurred",
                    traceId = context.TraceIdentifier,
                    timestamp = DateTimeOffset.UtcNow
                });
            }));

        // ── API key authentication (architecture rule #14: no localhost bypass) ──
        app.UseMiddleware<ApiKeyMiddleware>();

        // ── Endpoint groups ──
        app.MapTransactionEndpoints();
        app.MapPreAuthEndpoints();
        app.MapPumpStatusEndpoints();
        app.MapStatusEndpoints();

        return app;
    }
}

/// <summary>
/// Loads the LAN API key from <see cref="ICredentialStore"/> and injects it into
/// <see cref="LocalApiOptions.ApiKey"/>. Called lazily when the options are first
/// accessed (typically by <see cref="Auth.ApiKeyMiddleware"/>).
///
/// The key is cached after the first load to avoid repeated credential store lookups.
/// </summary>
internal sealed class CredentialStoreApiKeyPostConfigure : IPostConfigureOptions<LocalApiOptions>
{
    private readonly ICredentialStore _store;
    private string? _cachedKey;
    private bool _loaded;

    public CredentialStoreApiKeyPostConfigure(ICredentialStore store) => _store = store;

    public void PostConfigure(string? name, LocalApiOptions options)
    {
        if (!_loaded)
        {
            _cachedKey = _store.GetSecretAsync(CredentialKeys.LanApiKey).GetAwaiter().GetResult();
            _loaded = true;
        }

        if (!string.IsNullOrEmpty(_cachedKey))
            options.ApiKey = _cachedKey;
    }
}
