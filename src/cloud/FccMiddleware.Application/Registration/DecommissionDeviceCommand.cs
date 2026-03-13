using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.Registration;

public sealed class DecommissionDeviceCommand : IRequest<Result<DecommissionDeviceResult>>
{
    public required Guid DeviceId { get; init; }
    public required string DecommissionedBy { get; init; }

    /// <summary>
    /// FM-S04: Reason for decommissioning, recorded in the audit event.
    /// </summary>
    public required string Reason { get; init; }
}

public sealed class DecommissionDeviceResult
{
    public required Guid DeviceId { get; init; }
    public required DateTimeOffset DeactivatedAt { get; init; }
}
