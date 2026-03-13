using System.Security.Claims;
using FccMiddleware.Api.Portal;

namespace FccMiddleware.Api.Infrastructure;

/// <summary>
/// Enriches the ClaimsPrincipal with locally-managed role and legal entity claims
/// for portal (Entra) authenticated requests.
///
/// Runs after authentication, before authorization. Since Entra tokens do not carry
/// role claims, this middleware looks up the user by email in the database and injects
/// synthetic "roles" and "legal_entities" claims so existing authorization policies
/// continue to work unchanged.
/// </summary>
public sealed class PortalRoleEnrichmentMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PortalRoleEnrichmentMiddleware> _logger;

    public PortalRoleEnrichmentMiddleware(RequestDelegate next, ILogger<PortalRoleEnrichmentMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, PortalUserService portalUserService)
    {
        if (context.User.Identity is not { IsAuthenticated: true })
        {
            await _next(context);
            return;
        }

        // Device tokens carry "site" — skip those.
        if (context.User.HasClaim(c => c.Type == "site"))
        {
            await _next(context);
            return;
        }

        // Service auth (HMAC, API keys) — skip those.
        if (context.User.HasClaim(c => c.Type == "fcc_api_key_id") ||
            context.User.HasClaim(c => c.Type == "key_id"))
        {
            await _next(context);
            return;
        }

        // Extract email from Entra token — try multiple claim types.
        // Entra v2.0 tokens use "preferred_username" for the user's email.
        // Some configurations also emit "email" or "emails" (array serialized as multiple claims).
        var email = context.User.FindFirst("preferred_username")?.Value
                    ?? context.User.FindFirst("email")?.Value
                    ?? context.User.FindFirst("emails")?.Value
                    ?? context.User.FindFirst(ClaimTypes.Email)?.Value;

        if (string.IsNullOrEmpty(email))
        {
            await _next(context);
            return;
        }

        // Also grab oid if available — we'll back-fill it on the user record.
        var oid = context.User.FindFirst("oid")?.Value
                  ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? context.User.FindFirst("sub")?.Value;

        var userInfo = await portalUserService.GetByEmailAsync(email, oid, context.RequestAborted);

        if (userInfo is null)
        {
            _logger.LogWarning("Portal user not provisioned: {Email}", email);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                errorCode = "USER_NOT_PROVISIONED",
                message = "Your account has not been provisioned. Contact your FCC Admin.",
            });
            return;
        }

        // Inject synthetic claims into a new identity so authorization policies work.
        var enrichedIdentity = new ClaimsIdentity("PortalRoleEnrichment");
        enrichedIdentity.AddClaim(new Claim("roles", userInfo.RoleName));

        if (userInfo.AllLegalEntities)
        {
            enrichedIdentity.AddClaim(new Claim("legal_entities", "*"));
        }
        else if (userInfo.LegalEntityIds.Count > 0)
        {
            var legalEntitiesValue = string.Join(",", userInfo.LegalEntityIds);
            enrichedIdentity.AddClaim(new Claim("legal_entities", legalEntitiesValue));
        }

        context.User.AddIdentity(enrichedIdentity);

        await _next(context);
    }
}
