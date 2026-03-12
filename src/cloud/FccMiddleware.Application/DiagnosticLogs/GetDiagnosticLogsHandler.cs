using System.Text.Json;
using FccMiddleware.Application.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.DiagnosticLogs;

public sealed class GetDiagnosticLogsHandler : IRequestHandler<GetDiagnosticLogsQuery, Result<GetDiagnosticLogsQueryResult>>
{
    private readonly IDiagnosticLogsDbContext _db;
    private readonly ILogger<GetDiagnosticLogsHandler> _logger;

    public GetDiagnosticLogsHandler(IDiagnosticLogsDbContext db, ILogger<GetDiagnosticLogsHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<GetDiagnosticLogsQueryResult>> Handle(
        GetDiagnosticLogsQuery request,
        CancellationToken cancellationToken)
    {
        var logs = await _db.GetRecentDiagnosticLogsAsync(request.DeviceId, request.MaxBatches);

        var batches = logs.Select(log => new DiagnosticLogBatchResult
        {
            Id = log.Id,
            UploadedAtUtc = log.UploadedAtUtc,
            LogEntries = TryDeserializeEntries(log.LogEntriesJson),
        }).ToList();

        return Result<GetDiagnosticLogsQueryResult>.Success(new GetDiagnosticLogsQueryResult
        {
            DeviceId = request.DeviceId,
            Batches = batches,
        });
    }

    private static List<string> TryDeserializeEntries(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
