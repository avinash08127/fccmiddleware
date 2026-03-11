namespace VirtualLab.Domain.Models;

public sealed class LabEventLog
{
    public Guid Id { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? FccSimulatorProfileId { get; set; }
    public Guid? PreAuthSessionId { get; set; }
    public Guid? SimulatedTransactionId { get; set; }
    public Guid? ScenarioRunId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Severity { get; set; } = "Information";
    public string Category { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string RawPayloadJson { get; set; } = "{}";
    public string CanonicalPayloadJson { get; set; } = "{}";
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; set; }

    public Site? Site { get; set; }
    public FccSimulatorProfile? FccSimulatorProfile { get; set; }
    public PreAuthSession? PreAuthSession { get; set; }
    public SimulatedTransaction? SimulatedTransaction { get; set; }
    public ScenarioRun? ScenarioRun { get; set; }
}
