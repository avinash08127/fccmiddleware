namespace FccMiddleware.Contracts.Portal;

public sealed record DashboardSummaryDto
{
    public required TransactionVolumeDataDto TransactionVolume { get; init; }
    public required IngestionHealthDataDto IngestionHealth { get; init; }
    public required AgentStatusSummaryDataDto AgentStatus { get; init; }
    public required ReconciliationSummaryDataDto Reconciliation { get; init; }
    public required StaleTransactionsDataDto StaleTransactions { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
}

public sealed record TransactionVolumeDataDto
{
    public required IReadOnlyList<TransactionVolumeHourlyBucketDto> HourlyBuckets { get; init; }
}

public sealed record TransactionVolumeHourlyBucketDto
{
    public required DateTimeOffset Hour { get; init; }
    public required int Total { get; init; }
    public required TransactionVolumeBySourceDto BySource { get; init; }
}

public sealed record TransactionVolumeBySourceDto
{
    public required int FccPush { get; init; }
    public required int EdgeUpload { get; init; }
    public required int CloudPull { get; init; }
}

public sealed record IngestionHealthDataDto
{
    public required decimal TransactionsPerMinute { get; init; }
    public required decimal SuccessRate { get; init; }
    public required decimal ErrorRate { get; init; }
    public int? LatencyP95Ms { get; init; }
    public required int DlqDepth { get; init; }
    public required int PeriodMinutes { get; init; }
}

public sealed record AgentStatusSummaryDataDto
{
    public required int TotalAgents { get; init; }
    public required int Online { get; init; }
    public required int Degraded { get; init; }
    public required int Offline { get; init; }
    public required IReadOnlyList<OfflineAgentItemDto> OfflineAgents { get; init; }
}

public sealed record OfflineAgentItemDto
{
    public required Guid DeviceId { get; init; }
    public required string SiteCode { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public required string ConnectivityState { get; init; }
}

public sealed record ReconciliationSummaryDataDto
{
    public required int PendingExceptions { get; init; }
    public required int AutoApproved { get; init; }
    public required int Flagged { get; init; }
    public required DateTimeOffset LastUpdatedAt { get; init; }
}

public sealed record StaleTransactionsDataDto
{
    public required int Count { get; init; }
    public required string Trend { get; init; }
    public required int ThresholdMinutes { get; init; }
}

public sealed record DashboardAlertsResponseDto
{
    public required IReadOnlyList<DashboardAlertDto> Alerts { get; init; }
    public required int TotalCount { get; init; }
}

public sealed record DashboardAlertDto
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public string? SiteCode { get; init; }
    public Guid? LegalEntityId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
