using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.Registration;

public sealed class GenerateBootstrapTokenCommand : IRequest<Result<GenerateBootstrapTokenResult>>
{
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required string CreatedBy { get; init; }
}

public sealed class GenerateBootstrapTokenResult
{
    public required Guid TokenId { get; init; }
    public required string RawToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
