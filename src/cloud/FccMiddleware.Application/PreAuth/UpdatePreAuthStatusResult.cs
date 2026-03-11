using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Application.PreAuth;

public sealed record UpdatePreAuthStatusResult
{
    public required Guid PreAuthId { get; init; }
    public required PreAuthStatus Status { get; init; }
    public required string SiteCode { get; init; }
    public required string OdooOrderId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
