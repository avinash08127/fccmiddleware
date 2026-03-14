using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.Registration;

public sealed class ApproveSuspiciousDeviceCommand : IRequest<Result<SuspiciousDeviceReviewResult>>
{
    public required Guid DeviceId { get; init; }
    public required string ApprovedByActorId { get; init; }
    public string? ApprovedByActorDisplay { get; init; }
    public required string Reason { get; init; }
}

public sealed class SuspiciousDeviceReviewResult
{
    public required Guid DeviceId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
