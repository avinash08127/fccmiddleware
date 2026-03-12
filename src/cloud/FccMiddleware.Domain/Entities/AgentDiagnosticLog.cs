namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Stores diagnostic log entries uploaded from Edge Agent devices.
/// WARN/ERROR entries only, 7-day retention.
/// </summary>
public sealed class AgentDiagnosticLog
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = default!;
    public DateTimeOffset UploadedAtUtc { get; set; }

    /// <summary>
    /// JSONB array of JSONL log entry strings.
    /// </summary>
    public string LogEntriesJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
}
