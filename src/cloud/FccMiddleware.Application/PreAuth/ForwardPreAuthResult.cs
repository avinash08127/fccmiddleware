using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Application.PreAuth;

/// <summary>
/// Result of the ForwardPreAuth command.
/// </summary>
public sealed record ForwardPreAuthResult
{
    /// <summary>Middleware-assigned UUID for the pre-auth record.</summary>
    public required Guid PreAuthId { get; init; }

    /// <summary>Current status after processing.</summary>
    public required PreAuthStatus Status { get; init; }

    /// <summary>True when a new record was created; false when an existing record was updated.</summary>
    public required bool Created { get; init; }

    public required string SiteCode { get; init; }
    public required string OdooOrderId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
