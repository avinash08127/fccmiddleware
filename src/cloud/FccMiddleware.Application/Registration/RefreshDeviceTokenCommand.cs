using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.Registration;

public sealed class RefreshDeviceTokenCommand : IRequest<Result<RefreshDeviceTokenResult>>
{
    public required string RefreshToken { get; init; }
}

public sealed class RefreshDeviceTokenResult
{
    public required string DeviceToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset TokenExpiresAt { get; init; }
}
