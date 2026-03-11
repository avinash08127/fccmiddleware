using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Models;
using MediatR;

namespace FccMiddleware.Application.Telemetry;

public sealed class SubmitTelemetryCommand : IRequest<Result<SubmitTelemetryResult>>
{
    public required Guid DeviceId { get; init; }
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required TelemetryPayload Payload { get; init; }
}
