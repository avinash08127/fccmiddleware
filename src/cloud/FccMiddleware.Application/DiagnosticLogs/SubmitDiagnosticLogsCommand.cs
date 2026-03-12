using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.DiagnosticLogs;

public sealed class SubmitDiagnosticLogsCommand : IRequest<Result<SubmitDiagnosticLogsResult>>
{
    public Guid DeviceId { get; init; }
    public string SiteCode { get; init; } = default!;
    public Guid LegalEntityId { get; init; }
    public DateTimeOffset UploadedAtUtc { get; init; }
    public List<string> LogEntries { get; init; } = new();
}

public sealed record SubmitDiagnosticLogsResult
{
    public Guid BatchId { get; init; }
    public int EntriesStored { get; init; }
}
