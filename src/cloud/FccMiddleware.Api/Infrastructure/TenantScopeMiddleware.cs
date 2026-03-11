using System.Security.Claims;
using FccMiddleware.Infrastructure.Persistence;

namespace FccMiddleware.Api.Infrastructure;

public sealed class TenantScopeMiddleware
{
    private readonly RequestDelegate _next;

    public TenantScopeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        try
        {
            if (TryResolveTenant(context.User, out var legalEntityId))
            {
                tenantContext.SetTenant(legalEntityId);
            }
            else
            {
                tenantContext.ClearTenant();
            }

            await _next(context);
        }
        finally
        {
            tenantContext.ClearTenant();
        }
    }

    internal static bool TryResolveTenant(ClaimsPrincipal user, out Guid legalEntityId)
    {
        legalEntityId = Guid.Empty;

        var leiClaim = user.FindFirst("lei")?.Value;
        if (Guid.TryParse(leiClaim, out legalEntityId))
        {
            return true;
        }

        var legalEntities = user.FindAll("legal_entities")
            .SelectMany(claim => claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => value != "*")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return legalEntities.Length == 1
               && Guid.TryParse(legalEntities[0], out legalEntityId);
    }
}
