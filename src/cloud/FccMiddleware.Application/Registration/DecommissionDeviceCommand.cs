using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.Registration;

public sealed class DecommissionDeviceCommand : IRequest<Result<DecommissionDeviceResult>>
{
    public required Guid DeviceId { get; init; }
}

public sealed class DecommissionDeviceResult
{
    public required Guid DeviceId { get; init; }
    public required DateTimeOffset DeactivatedAt { get; init; }
}
