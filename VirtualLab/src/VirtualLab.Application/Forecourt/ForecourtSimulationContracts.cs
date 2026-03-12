using VirtualLab.Domain.Enums;

namespace VirtualLab.Application.Forecourt;

public sealed class NozzleLiftRequest
{
    public string? CorrelationId { get; init; }
    public bool ForceFault { get; init; }
    public string? FaultMessage { get; init; }
}

public sealed class NozzleHangRequest
{
    public string? CorrelationId { get; init; }
    public int? ElapsedSeconds { get; init; }
    public bool ClearFault { get; init; }
}

public sealed class DispenseSimulationRequest
{
    public string Action { get; init; } = "start";
    public string? CorrelationId { get; init; }
    public decimal? FlowRateLitresPerMinute { get; init; }
    public decimal? TargetAmount { get; init; }
    public decimal? TargetVolume { get; init; }
    public int? ElapsedSeconds { get; init; }
    public bool InjectDuplicate { get; init; }
    public bool SimulateFailure { get; init; }
    public string? FailureMessage { get; init; }
    public bool ForceFault { get; init; }
}

public sealed class PushTransactionsRequest
{
    public IReadOnlyList<string>? TransactionIds { get; init; }
    public string? TargetKey { get; init; }
}

public sealed class AcknowledgeTransactionsRequest
{
    public IReadOnlyList<string> TransactionIds { get; init; } = [];
    public string? CorrelationId { get; init; }
}

public sealed record NozzleSimulationSnapshot(
    Guid SiteId,
    Guid PumpId,
    Guid NozzleId,
    string SiteCode,
    int PumpNumber,
    int NozzleNumber,
    string Label,
    NozzleState State,
    string ProductCode,
    string ProductName,
    decimal UnitPrice,
    string CurrencyCode,
    string CorrelationId,
    Guid? PreAuthSessionId,
    string SimulationStateJson,
    DateTimeOffset UpdatedAtUtc);

public sealed record TransactionSimulationSummary(
    Guid Id,
    string ExternalTransactionId,
    string CorrelationId,
    TransactionDeliveryMode DeliveryMode,
    SimulatedTransactionStatus Status,
    decimal Volume,
    decimal TotalAmount,
    decimal UnitPrice,
    DateTimeOffset OccurredAtUtc,
    string RawPayloadJson,
    string CanonicalPayloadJson,
    string MetadataJson,
    string TimelineJson);

public sealed record NozzleActionResult(
    int StatusCode,
    string Message,
    NozzleSimulationSnapshot? Nozzle,
    TransactionSimulationSummary? Transaction,
    bool TransactionGenerated,
    bool Faulted,
    string CorrelationId);

public sealed record PushTransactionAttemptSummary(
    string ExternalTransactionId,
    string CorrelationId,
    string TargetKey,
    string Status,
    bool DuplicateInjected,
    int AttemptNumber,
    int RetryCount,
    int ResponseStatusCode,
    bool Acknowledged,
    DateTimeOffset? NextRetryAtUtc);

public sealed record PushTransactionsResult(
    int StatusCode,
    string Message,
    int PushedCount,
    IReadOnlyList<PushTransactionAttemptSummary> Attempts);

public sealed record PullTransactionsResult(
    int StatusCode,
    string ContentType,
    string ResponseBody);

public sealed record AcknowledgeTransactionsResult(
    int StatusCode,
    string ContentType,
    string ResponseBody);

public sealed record FccEndpointResult(
    int StatusCode,
    string ContentType,
    string ResponseBody);

public interface IForecourtSimulationService
{
    Task<NozzleActionResult> LiftAsync(Guid siteId, Guid pumpId, Guid nozzleId, NozzleLiftRequest request, CancellationToken cancellationToken = default);
    Task<NozzleActionResult> HangAsync(Guid siteId, Guid pumpId, Guid nozzleId, NozzleHangRequest request, CancellationToken cancellationToken = default);
    Task<NozzleActionResult> DispenseAsync(Guid siteId, Guid pumpId, Guid nozzleId, DispenseSimulationRequest request, CancellationToken cancellationToken = default);
    Task<PushTransactionsResult> PushTransactionsAsync(Guid siteId, PushTransactionsRequest request, CancellationToken cancellationToken = default);
    Task<PullTransactionsResult> PullTransactionsAsync(string siteCode, int limit, string? cursor, CancellationToken cancellationToken = default);
    Task<AcknowledgeTransactionsResult> AcknowledgeTransactionsAsync(string siteCode, AcknowledgeTransactionsRequest request, CancellationToken cancellationToken = default);
    Task<FccEndpointResult> GetPumpStatusAsync(string siteCode, CancellationToken cancellationToken = default);
    Task<FccEndpointResult> GetHealthAsync(string siteCode, CancellationToken cancellationToken = default);
}
