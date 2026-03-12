using VirtualLab.Domain.Enums;

namespace VirtualLab.Domain.Models;

public sealed class PreAuthSession
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid? PumpId { get; set; }
    public Guid? NozzleId { get; set; }
    public Guid? ScenarioRunId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string ExternalReference { get; set; } = string.Empty;
    public PreAuthFlowMode Mode { get; set; }
    public PreAuthSessionStatus Status { get; set; }
    public decimal ReservedAmount { get; set; }
    public decimal? AuthorizedAmount { get; set; }
    public decimal? FinalAmount { get; set; }
    public decimal? FinalVolume { get; set; }
    public string RawRequestJson { get; set; } = "{}";
    public string CanonicalRequestJson { get; set; } = "{}";
    public string RawResponseJson { get; set; } = "{}";
    public string CanonicalResponseJson { get; set; } = "{}";
    public string TimelineJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? AuthorizedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public Site Site { get; set; } = null!;
    public Pump? Pump { get; set; }
    public Nozzle? Nozzle { get; set; }
    public ScenarioRun? ScenarioRun { get; set; }
    public ICollection<SimulatedTransaction> Transactions { get; set; } = new List<SimulatedTransaction>();
}
