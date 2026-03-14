using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.Registration;

public sealed class RejectSuspiciousDeviceCommand : IRequest<Result<SuspiciousDeviceReviewResult>>
{
    public required Guid DeviceId { get; init; }
    public required string RejectedByActorId { get; init; }
    public string? RejectedByActorDisplay { get; init; }
    public required string Reason { get; init; }
}
