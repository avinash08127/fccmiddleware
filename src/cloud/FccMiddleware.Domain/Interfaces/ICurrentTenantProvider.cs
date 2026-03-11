namespace FccMiddleware.Domain.Interfaces;

/// <summary>
/// Provides the current legal entity (tenant) scope for a request or operation.
/// Implemented in Infrastructure; populated by ASP.NET Core middleware from the JWT claim.
/// A null value means no tenant is scoped (e.g., background workers or admin operations).
/// </summary>
public interface ICurrentTenantProvider
{
    Guid? CurrentLegalEntityId { get; }
}
