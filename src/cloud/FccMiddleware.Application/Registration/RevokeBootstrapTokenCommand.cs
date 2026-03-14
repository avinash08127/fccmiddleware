using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.Registration;

public sealed class RevokeBootstrapTokenCommand : IRequest<Result<RevokeBootstrapTokenResult>>
{
    public required Guid TokenId { get; init; }
    public required string RevokedBy { get; init; }
    public string? RevokedByActorId { get; init; }
    public string? RevokedByActorDisplay { get; init; }
}

public sealed class RevokeBootstrapTokenResult
{
    public required Guid TokenId { get; init; }
    public required DateTimeOffset RevokedAt { get; init; }
}
