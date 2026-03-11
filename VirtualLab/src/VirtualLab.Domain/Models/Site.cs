using VirtualLab.Domain.Enums;

namespace VirtualLab.Domain.Models;

public sealed class Site
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public Guid ActiveFccSimulatorProfileId { get; set; }
    public string SiteCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public string CurrencyCode { get; set; } = "USD";
    public string ExternalReference { get; set; } = string.Empty;
    public SimulatedAuthMode InboundAuthMode { get; set; }
    public string ApiKeyHeaderName { get; set; } = string.Empty;
    public string ApiKeyValue { get; set; } = string.Empty;
    public string BasicAuthUsername { get; set; } = string.Empty;
    public string BasicAuthPassword { get; set; } = string.Empty;
    public TransactionDeliveryMode DeliveryMode { get; set; }
    public PreAuthFlowMode PreAuthMode { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public LabEnvironment LabEnvironment { get; set; } = null!;
    public FccSimulatorProfile ActiveFccSimulatorProfile { get; set; } = null!;
    public ICollection<Pump> Pumps { get; set; } = new List<Pump>();
    public ICollection<PreAuthSession> PreAuthSessions { get; set; } = new List<PreAuthSession>();
    public ICollection<SimulatedTransaction> Transactions { get; set; } = new List<SimulatedTransaction>();
    public ICollection<CallbackTarget> CallbackTargets { get; set; } = new List<CallbackTarget>();
    public ICollection<LabEventLog> EventLogs { get; set; } = new List<LabEventLog>();
    public ICollection<ScenarioRun> ScenarioRuns { get; set; } = new List<ScenarioRun>();
}
