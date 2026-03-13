namespace FccMiddleware.Domain.Interfaces;

/// <summary>
/// Marker interface for entities that belong to a specific legal entity (tenant).
/// Used by <see cref="PortalQueryExtensions"/> to enforce tenant scoping on portal queries,
/// reducing the risk of data leakage when IgnoreQueryFilters is used.
/// </summary>
public interface ITenantScoped
{
    Guid LegalEntityId { get; }
}
