using VirtualLab.Domain.Enums;

namespace VirtualLab.Domain.Models;

public sealed class SimulatedTransaction
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid PumpId { get; set; }
    public Guid NozzleId { get; set; }
    public Guid ProductId { get; set; }
    public Guid? PreAuthSessionId { get; set; }
    public Guid? ScenarioRunId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string ExternalTransactionId { get; set; } = string.Empty;
    public TransactionDeliveryMode DeliveryMode { get; set; }
    public SimulatedTransactionStatus Status { get; set; }
    public decimal Volume { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public string RawPayloadJson { get; set; } = "{}";
    public string CanonicalPayloadJson { get; set; } = "{}";
    public string RawHeadersJson { get; set; } = "{}";
    public string DeliveryCursor { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public string TimelineJson { get; set; } = "[]";

    public Site Site { get; set; } = null!;
    public Pump Pump { get; set; } = null!;
    public Nozzle Nozzle { get; set; } = null!;
    public Product Product { get; set; } = null!;
    public PreAuthSession? PreAuthSession { get; set; }
    public ScenarioRun? ScenarioRun { get; set; }
    public ICollection<CallbackAttempt> CallbackAttempts { get; set; } = new List<CallbackAttempt>();
}
