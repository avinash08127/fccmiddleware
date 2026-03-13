using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Api.Portal;

public sealed class PortalAccessResolver
{
    private readonly ILogger<PortalAccessResolver> _logger;

    public PortalAccessResolver(ILogger<PortalAccessResolver> logger)
    {
        _logger = logger;
    }

    public PortalAccess Resolve(ClaimsPrincipal user)
    {
        var roles = user.FindAll("roles")
            .SelectMany(claim => claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allowAll = roles.Contains("FccAdmin");
        var legalEntityClaims = user.FindAll("legal_entities")
            .Select(claim => claim.Value)
            .ToList();

        if (allowAll && (legalEntityClaims.Count == 0 || legalEntityClaims.Any(value => value.Trim() == "*")))
        {
            // FM-S07: Log when an FccAdmin is granted unrestricted cross-tenant access
            // so that compromised admin sessions are traceable in audit logs.
            var userId = ResolveUserId(user) ?? "(unknown)";
            _logger.LogWarning(
                "FccAdmin granted AllowAllLegalEntities access. UserId={UserId}, HasLegalEntityClaims={HasClaims}",
                userId,
                legalEntityClaims.Count > 0);
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

    /// <summary>
    /// Returns true if the user has a role that grants access to sensitive operational
    /// payloads (raw DLQ payloads, full telemetry, audit event payloads).
    /// FccViewer users receive redacted/summary views only.
    /// </summary>
    public static bool HasSensitiveDataAccess(ClaimsPrincipal user)
    {
        var roles = user.FindAll("roles")
            .SelectMany(claim => claim.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return roles.Overlaps(SensitiveDataRoles);
    }

    private static readonly HashSet<string> SensitiveDataRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "FccAdmin",
        "FccUser",
    };
}

public sealed record PortalAccess(
    bool IsValid,
    IReadOnlyCollection<Guid> ScopedLegalEntityIds,
    bool AllowAllLegalEntities)
{
    public bool CanAccess(Guid legalEntityId) =>
        AllowAllLegalEntities || ScopedLegalEntityIds.Contains(legalEntityId);
}
