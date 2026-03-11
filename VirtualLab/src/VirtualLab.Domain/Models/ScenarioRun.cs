using VirtualLab.Domain.Enums;

namespace VirtualLab.Domain.Models;

public sealed class ScenarioRun
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid ScenarioDefinitionId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public int ReplaySeed { get; set; }
    public string ReplaySignature { get; set; } = string.Empty;
    public ScenarioRunStatus Status { get; set; }
    public string InputSnapshotJson { get; set; } = "{}";
    public string ResultSummaryJson { get; set; } = "{}";
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public Site Site { get; set; } = null!;
    public ScenarioDefinition ScenarioDefinition { get; set; } = null!;
    public ICollection<PreAuthSession> PreAuthSessions { get; set; } = new List<PreAuthSession>();
    public ICollection<SimulatedTransaction> Transactions { get; set; } = new List<SimulatedTransaction>();
    public ICollection<LabEventLog> EventLogs { get; set; } = new List<LabEventLog>();
}
