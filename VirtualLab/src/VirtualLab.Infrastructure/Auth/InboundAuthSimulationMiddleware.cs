using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Domain.Profiles;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Infrastructure.Auth;

public sealed class InboundAuthSimulationMiddleware(RequestDelegate next, ILogger<InboundAuthSimulationMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context, VirtualLabDbContext dbContext)
    {
        InboundAuthTarget? target = await ResolveTargetAsync(context, dbContext, context.RequestAborted);
        if (target is null || target.Mode == SimulatedAuthMode.None)
        {
            await next(context);
            return;
        }

        AuthCheckResult result = ValidateRequest(context.Request, target);
        if (result.IsAuthorized)
        {
            await next(context);
            return;
        }

        string correlationId = InboundAuthRequestSanitizer.ResolveCorrelationId(context);
        LabEventLog entry = new()
        {
            Id = Guid.NewGuid(),
            SiteId = target.SiteId,
            FccSimulatorProfileId = target.ProfileId,
            CorrelationId = correlationId,
            Severity = "Warning",
            Category = "AuthFailure",
            EventType = target.TargetType == "Callback" ? "CallbackAuthRejected" : "FccAuthRejected",
            Message = $"{target.TargetType} request authentication failed for '{target.TargetKey}'.",
            RawPayloadJson = "{}",
            CanonicalPayloadJson = "{}",
            MetadataJson = InboundAuthRequestSanitizer.BuildSafeMetadataJson(
                context,
                target.TargetType,
                target.TargetKey,
                target.Mode.ToString(),
                result.FailureReason,
                target.CallbackTargetId,
                target.SiteId,
                target.ProfileId),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        };

        dbContext.LabEventLogs.Add(entry);
        await dbContext.SaveChangesAsync(context.RequestAborted);

        logger.LogWarning(
            "Auth simulation rejected request for {TargetType} target {TargetKey}. Reason: {FailureReason}. Trace: {TraceIdentifier}",
            target.TargetType,
            target.TargetKey,
            result.FailureReason,
            context.TraceIdentifier);

        if (target.Mode == SimulatedAuthMode.BasicAuth)
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"VirtualLab\"";
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(
                new
                {
                    error = "unauthorized",
                    message = "Inbound authentication simulation rejected the request.",
                },
                JsonOptions));
    }

    private static async Task<InboundAuthTarget?> ResolveTargetAsync(HttpContext context, VirtualLabDbContext dbContext, CancellationToken cancellationToken)
    {
        string[] segments = context.Request.Path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        if (segments.Length >= 2 && string.Equals(segments[0], "fcc", StringComparison.OrdinalIgnoreCase))
        {
            string siteCode = segments[1];
            var resolved = await dbContext.Sites
                .AsNoTracking()
                .Where(x => x.SiteCode == siteCode && x.IsActive)
                .Select(x => new
                {
                    x.Id,
                    x.SiteCode,
                    x.InboundAuthMode,
                    x.ApiKeyHeaderName,
                    x.ApiKeyValue,
                    x.BasicAuthUsername,
                    x.BasicAuthPassword,
                    ProfileId = x.ActiveFccSimulatorProfile.Id,
                    ProfileAuthJson = x.ActiveFccSimulatorProfile.AuthConfigurationJson,
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (resolved is null)
            {
                return null;
            }

            FccAuthConfiguration profileAuth = DeserializeAuthConfiguration(resolved.ProfileAuthJson);
            bool useSiteOverride =
                resolved.InboundAuthMode != profileAuth.Mode ||
                !string.IsNullOrWhiteSpace(resolved.ApiKeyHeaderName) ||
                !string.IsNullOrWhiteSpace(resolved.ApiKeyValue) ||
                !string.IsNullOrWhiteSpace(resolved.BasicAuthUsername) ||
                !string.IsNullOrWhiteSpace(resolved.BasicAuthPassword);

            return useSiteOverride
                ? new InboundAuthTarget(
                    "Fcc",
                    resolved.SiteCode,
                    resolved.InboundAuthMode,
                    resolved.ApiKeyHeaderName,
                    resolved.ApiKeyValue,
                    resolved.BasicAuthUsername,
                    resolved.BasicAuthPassword,
                    resolved.Id,
                    resolved.ProfileId,
                    null)
                : new InboundAuthTarget(
                    "Fcc",
                    resolved.SiteCode,
                    profileAuth.Mode,
                    profileAuth.ApiKeyHeaderName,
                    profileAuth.ApiKeyValue,
                    profileAuth.BasicAuthUsername,
                    profileAuth.BasicAuthPassword,
                    resolved.Id,
                    resolved.ProfileId,
                    null);
        }

        if (segments.Length >= 2 && string.Equals(segments[0], "callbacks", StringComparison.OrdinalIgnoreCase))
        {
            string targetKey = segments[1];
            CallbackTarget? target = await dbContext.CallbackTargets
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.TargetKey == targetKey && x.IsActive, cancellationToken);

            return target is null
                ? null
                : new InboundAuthTarget(
                    "Callback",
                    target.TargetKey,
                    target.AuthMode,
                    target.ApiKeyHeaderName,
                    target.ApiKeyValue,
                    target.BasicAuthUsername,
                    target.BasicAuthPassword,
                    target.SiteId,
                    null,
                    target.Id);
        }

        return null;
    }

    private static AuthCheckResult ValidateRequest(HttpRequest request, InboundAuthTarget target)
    {
        return target.Mode switch
        {
            SimulatedAuthMode.None => AuthCheckResult.Authorized(),
            SimulatedAuthMode.ApiKey => ValidateApiKey(request, target),
            SimulatedAuthMode.BasicAuth => ValidateBasicAuth(request, target),
            _ => AuthCheckResult.Rejected("Unsupported auth mode."),
        };
    }

    private static AuthCheckResult ValidateApiKey(HttpRequest request, InboundAuthTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.ApiKeyHeaderName) || string.IsNullOrWhiteSpace(target.ApiKeyValue))
        {
            return AuthCheckResult.Rejected("API key auth is configured without a complete header name/value pair.");
        }

        if (!request.Headers.TryGetValue(target.ApiKeyHeaderName, out Microsoft.Extensions.Primitives.StringValues providedValue))
        {
            return AuthCheckResult.Rejected($"Missing API key header '{target.ApiKeyHeaderName}'.");
        }

        return string.Equals(providedValue.ToString(), target.ApiKeyValue, StringComparison.Ordinal)
            ? AuthCheckResult.Authorized()
            : AuthCheckResult.Rejected($"Invalid API key for header '{target.ApiKeyHeaderName}'.");
    }

    private static AuthCheckResult ValidateBasicAuth(HttpRequest request, InboundAuthTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.BasicAuthUsername) || string.IsNullOrWhiteSpace(target.BasicAuthPassword))
        {
            return AuthCheckResult.Rejected("Basic auth is configured without a complete username/password pair.");
        }

        if (!request.Headers.TryGetValue("Authorization", out Microsoft.Extensions.Primitives.StringValues headerValue))
        {
            return AuthCheckResult.Rejected("Missing Authorization header.");
        }

        if (!AuthenticationHeaderValue.TryParse(headerValue.ToString(), out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter))
        {
            return AuthCheckResult.Rejected("Authorization header is not a valid Basic auth header.");
        }

        string decodedCredentials;
        try
        {
            decodedCredentials = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return AuthCheckResult.Rejected("Authorization header contains invalid base64 credentials.");
        }

        int separatorIndex = decodedCredentials.IndexOf(':');
        if (separatorIndex <= 0)
        {
            return AuthCheckResult.Rejected("Authorization header is missing the username/password separator.");
        }

        string username = decodedCredentials[..separatorIndex];
        string password = decodedCredentials[(separatorIndex + 1)..];

        return string.Equals(username, target.BasicAuthUsername, StringComparison.Ordinal) &&
               string.Equals(password, target.BasicAuthPassword, StringComparison.Ordinal)
            ? AuthCheckResult.Authorized()
            : AuthCheckResult.Rejected("Invalid Basic auth credentials.");
    }

    private static FccAuthConfiguration DeserializeAuthConfiguration(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new FccAuthConfiguration();
        }

        try
        {
            return JsonSerializer.Deserialize<FccAuthConfiguration>(json, JsonOptions) ?? new FccAuthConfiguration();
        }
        catch (JsonException)
        {
            return new FccAuthConfiguration();
        }
    }

    private sealed record InboundAuthTarget(
        string TargetType,
        string TargetKey,
        SimulatedAuthMode Mode,
        string ApiKeyHeaderName,
        string ApiKeyValue,
        string BasicAuthUsername,
        string BasicAuthPassword,
        Guid? SiteId,
        Guid? ProfileId,
        Guid? CallbackTargetId);

    private sealed record AuthCheckResult(bool IsAuthorized, string FailureReason)
    {
        public static AuthCheckResult Authorized() => new(true, string.Empty);
        public static AuthCheckResult Rejected(string failureReason) => new(false, failureReason);
    }
}
