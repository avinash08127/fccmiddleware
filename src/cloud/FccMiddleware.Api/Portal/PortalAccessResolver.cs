using System.Security.Claims;

namespace FccMiddleware.Api.Portal;

public sealed class PortalAccessResolver
{
    public PortalAccess Resolve(ClaimsPrincipal user)
    {
        var roles = user.FindAll("roles")
            .SelectMany(claim => claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allowAll = roles.Contains("SystemAdmin") || roles.Contains("SystemAdministrator");
        var legalEntityClaims = user.FindAll("legal_entities")
            .Select(claim => claim.Value)
            .ToList();

        if (allowAll && (legalEntityClaims.Count == 0 || legalEntityClaims.Any(value => value.Trim() == "*")))
        {
            return new PortalAccess(true, Array.Empty<Guid>(), true);
        }

        var ids = legalEntityClaims
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => value != "*")
            .Select(value => Guid.TryParse(value, out var parsed) ? parsed : Guid.Empty)
            .Where(value => value != Guid.Empty)
            .Distinct()
            .ToArray();

        return new PortalAccess(ids.Length > 0 || allowAll, ids, allowAll);
    }

    public string? ResolveUserId(ClaimsPrincipal user) =>
        user.FindFirstValue("oid")
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub")
        ?? user.FindFirstValue("preferred_username")
        ?? user.Identity?.Name;
}

public sealed record PortalAccess(
    bool IsValid,
    IReadOnlyCollection<Guid> ScopedLegalEntityIds,
    bool AllowAllLegalEntities)
{
    public bool CanAccess(Guid legalEntityId) =>
        AllowAllLegalEntities || ScopedLegalEntityIds.Contains(legalEntityId);
}
