namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Banknote Acceptor (BNA) report received from edge agent.
/// Used for cash reconciliation at fuel stations.
/// </summary>
public class BnaReportRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = "";
    public string TerminalId { get; set; } = "";
    public int NotesAccepted { get; set; }
    public DateTimeOffset ReportedAtUtc { get; set; }
    public string DeviceId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
