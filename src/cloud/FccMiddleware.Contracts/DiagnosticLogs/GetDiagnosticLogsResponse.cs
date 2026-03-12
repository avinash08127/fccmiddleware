namespace FccMiddleware.Contracts.DiagnosticLogs;

public sealed record GetDiagnosticLogsResponse
{
    public Guid DeviceId { get; init; }
    public List<DiagnosticLogBatch> Batches { get; init; } = new();
}

public sealed record DiagnosticLogBatch
{
    public Guid Id { get; init; }
    public DateTimeOffset UploadedAtUtc { get; init; }
    public List<string> LogEntries { get; init; } = new();
}
