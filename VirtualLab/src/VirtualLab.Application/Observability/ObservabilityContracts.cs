using VirtualLab.Application.Forecourt;
using VirtualLab.Application.ContractValidation;
using VirtualLab.Domain.Enums;

namespace VirtualLab.Application.Observability;

public sealed class TransactionListQuery
{
    public Guid? SiteId { get; init; }
    public string? SiteCode { get; init; }
    public string? CorrelationId { get; init; }
    public string? Search { get; init; }
    public TransactionDeliveryMode? DeliveryMode { get; init; }
    public SimulatedTransactionStatus? Status { get; init; }
    public int Limit { get; init; } = 50;
}

public sealed class LogListQuery
{
    public Guid? SiteId { get; init; }
    public string? SiteCode { get; init; }
    public Guid? ProfileId { get; init; }
    public string? Category { get; init; }
    public string? Severity { get; init; }
    public string? CorrelationId { get; init; }
    public string? Search { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed record TransactionListItemView(
    Guid Id,
    Guid SiteId,
    string SiteCode,
    string SiteName,
    Guid ProfileId,
    string ProfileKey,
    string ExternalTransactionId,
    string CorrelationId,
    int PumpNumber,
    int NozzleNumber,
    string ProductCode,
    string ProductName,
    TransactionDeliveryMode DeliveryMode,
    SimulatedTransactionStatus Status,
    decimal Volume,
    decimal TotalAmount,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    Guid? PreAuthSessionId,
    int CallbackAttemptCount,
    string? LastCallbackStatus,
    string RawPayloadJson,
    string CanonicalPayloadJson,
    PayloadContractValidationReport ContractValidation,
    string MetadataJson,
    string TimelineJson);

public sealed record TransactionCallbackAttemptView(
    Guid Id,
    Guid CallbackTargetId,
    string TargetKey,
    string TargetName,
    string CorrelationId,
    int AttemptNumber,
    CallbackAttemptStatus Status,
    int ResponseStatusCode,
    string RequestUrl,
    string RequestHeadersJson,
    string RequestPayloadJson,
    string ResponseHeadersJson,
    string ResponsePayloadJson,
    string ErrorMessage,
    int RetryCount,
    int MaxRetryCount,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? NextRetryAtUtc,
    DateTimeOffset? AcknowledgedAtUtc);

public sealed record TransactionTimelineEntryView(
    string Source,
    DateTimeOffset OccurredAtUtc,
    string Category,
    string EventType,
    string Severity,
    string State,
    string Message,
    string MetadataJson);

public sealed record TransactionDetailView(
    Guid Id,
    Guid SiteId,
    string SiteCode,
    string SiteName,
    Guid ProfileId,
    string ProfileKey,
    string ProfileName,
    string ExternalTransactionId,
    string CorrelationId,
    int PumpNumber,
    int NozzleNumber,
    string ProductCode,
    string ProductName,
    decimal UnitPrice,
    string CurrencyCode,
    TransactionDeliveryMode DeliveryMode,
    SimulatedTransactionStatus Status,
    decimal Volume,
    decimal TotalAmount,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    Guid? PreAuthSessionId,
    string RawHeadersJson,
    string RawPayloadJson,
    string CanonicalPayloadJson,
    PayloadContractValidationReport ContractValidation,
    string MetadataJson,
    IReadOnlyList<TransactionCallbackAttemptView> CallbackAttempts,
    IReadOnlyList<TransactionTimelineEntryView> Timeline);

public sealed record TransactionReplayRequest(
    string? CorrelationId);

public sealed record TransactionReplayResult(
    Guid TransactionId,
    string ExternalTransactionId,
    string CorrelationId,
    string Message);

public sealed record LogListItemView(
    Guid Id,
    Guid? SiteId,
    string? SiteCode,
    Guid? ProfileId,
    string? ProfileKey,
    Guid? SimulatedTransactionId,
    Guid? PreAuthSessionId,
    string Category,
    string EventType,
    string Severity,
    string Message,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed record LogDetailView(
    Guid Id,
    Guid? SiteId,
    string? SiteCode,
    Guid? ProfileId,
    string? ProfileKey,
    string? ProfileName,
    Guid? SimulatedTransactionId,
    string? ExternalTransactionId,
    Guid? PreAuthSessionId,
    string Category,
    string EventType,
    string Severity,
    string Message,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string RawPayloadJson,
    string CanonicalPayloadJson,
    string MetadataJson,
    string? RequestHeadersJson,
    string? RequestPayloadJson,
    string? ResponseHeadersJson,
    string? ResponsePayloadJson);

public interface IObservabilityService
{
    Task<IReadOnlyList<TransactionListItemView>> ListTransactionsAsync(TransactionListQuery query, CancellationToken cancellationToken = default);
    Task<TransactionDetailView?> GetTransactionAsync(Guid transactionId, CancellationToken cancellationToken = default);
    Task<TransactionReplayResult?> ReplayTransactionAsync(Guid transactionId, TransactionReplayRequest request, CancellationToken cancellationToken = default);
    Task<PushTransactionsResult> RepushTransactionAsync(Guid transactionId, string? targetKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LogListItemView>> ListLogsAsync(LogListQuery query, CancellationToken cancellationToken = default);
    Task<LogDetailView?> GetLogAsync(Guid logId, CancellationToken cancellationToken = default);
}
