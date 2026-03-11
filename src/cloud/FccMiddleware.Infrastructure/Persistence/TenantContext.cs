using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Infrastructure.Persistence;

/// <summary>
/// Scoped service that holds the current tenant (legal entity) for a request.
/// Populated by ASP.NET Core middleware that reads the 'legal_entity_id' JWT claim.
/// Injected into FccMiddlewareDbContext to drive global query filters.
/// For background workers (no HTTP context), CurrentLegalEntityId remains null,
/// which disables the tenant filter and allows cross-tenant queries.
/// </summary>
public sealed class TenantContext : ICurrentTenantProvider
{
    public Guid? CurrentLegalEntityId { get; set; }

    /// <summary>
    /// Sets the tenant scope. Called once per request by the tenant middleware.
    /// </summary>
    public void SetTenant(Guid legalEntityId) => CurrentLegalEntityId = legalEntityId;

    /// <summary>
    /// Clears the tenant scope. Used by background workers to run cross-tenant queries.
    /// </summary>
    public void ClearTenant() => CurrentLegalEntityId = null;
}
