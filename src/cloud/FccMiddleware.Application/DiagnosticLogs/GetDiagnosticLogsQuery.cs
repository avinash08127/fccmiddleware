using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.DiagnosticLogs;

public sealed class GetDiagnosticLogsQuery : IRequest<Result<GetDiagnosticLogsQueryResult>>
{
    public Guid DeviceId { get; init; }
    public int MaxBatches { get; init; } = 10;
}

public sealed record GetDiagnosticLogsQueryResult
{
    public Guid DeviceId { get; init; }
    public List<DiagnosticLogBatchResult> Batches { get; init; } = new();
}

public sealed record DiagnosticLogBatchResult
{
    public Guid Id { get; init; }
    public DateTimeOffset UploadedAtUtc { get; init; }
    public List<string> LogEntries { get; init; } = new();
}
