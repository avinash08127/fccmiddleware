using System.Text.Json;
using FccMiddleware.Application.Common;
using FccMiddleware.Domain.Entities;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Application.DiagnosticLogs;

public sealed class SubmitDiagnosticLogsHandler : IRequestHandler<SubmitDiagnosticLogsCommand, Result<SubmitDiagnosticLogsResult>>
{
    private readonly IDiagnosticLogsDbContext _db;
    private readonly ILogger<SubmitDiagnosticLogsHandler> _logger;

    public SubmitDiagnosticLogsHandler(IDiagnosticLogsDbContext db, ILogger<SubmitDiagnosticLogsHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Result<SubmitDiagnosticLogsResult>> Handle(
        SubmitDiagnosticLogsCommand request,
        CancellationToken cancellationToken)
    {
        var agent = await _db.FindAgentByDeviceIdAsync(request.DeviceId);
        if (agent is null || !agent.IsActive)
        {
            return Result<SubmitDiagnosticLogsResult>.Failure("DEVICE_NOT_FOUND",
                "No active agent registration found for the specified device.");
        }

        if (!string.Equals(agent.SiteCode, request.SiteCode, StringComparison.Ordinal))
        {
            return Result<SubmitDiagnosticLogsResult>.Failure("SITE_MISMATCH",
                "Device site code does not match the registered site.");
        }

        var logEntry = new AgentDiagnosticLog
        {
            Id = Guid.CreateVersion7(),
            DeviceId = request.DeviceId,
            LegalEntityId = request.LegalEntityId,
            SiteCode = request.SiteCode,
            UploadedAtUtc = request.UploadedAtUtc,
            LogEntriesJson = JsonSerializer.Serialize(request.LogEntries),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.AddDiagnosticLog(logEntry);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Diagnostic logs stored. DeviceId={DeviceId} SiteCode={SiteCode} Entries={EntryCount}",
            request.DeviceId, request.SiteCode, request.LogEntries.Count);

        return Result<SubmitDiagnosticLogsResult>.Success(new SubmitDiagnosticLogsResult
        {
            BatchId = logEntry.Id,
            EntriesStored = request.LogEntries.Count,
        });
    }
}
