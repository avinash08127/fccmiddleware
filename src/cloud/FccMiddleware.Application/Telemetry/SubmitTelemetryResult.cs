namespace FccMiddleware.Application.Telemetry;

public sealed class SubmitTelemetryResult
{
    public required Guid CorrelationId { get; init; }
    public required bool IsDuplicate { get; init; }
}
