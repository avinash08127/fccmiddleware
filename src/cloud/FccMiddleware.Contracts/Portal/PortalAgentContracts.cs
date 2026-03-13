using System.Text.Json;

namespace FccMiddleware.Contracts.Portal;

public sealed record AgentRegistrationDto
{
    public required Guid Id { get; init; }
    public required Guid DeviceId { get; init; }
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required string DeviceSerialNumber { get; init; }
    public required string DeviceModel { get; init; }
    public required string OsVersion { get; init; }
    public required string AgentVersion { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset RegisteredAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}

public sealed record AgentHealthSummaryDto
{
    public required Guid DeviceId { get; init; }
    public required string SiteCode { get; init; }
    public string? SiteName { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required string AgentVersion { get; init; }
    public required string Status { get; init; }
    public required bool HasTelemetry { get; init; }
    public string? ConnectivityState { get; init; }
    public int? BatteryPercent { get; init; }
    public bool? IsCharging { get; init; }
    public int? BufferDepth { get; init; }
    public int? SyncLagSeconds { get; init; }
    public DateTimeOffset? LastTelemetryAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}

public sealed record AgentTelemetryDto
{
    public required string SchemaVersion { get; init; }
    public required Guid DeviceId { get; init; }
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required DateTimeOffset ReportedAtUtc { get; init; }
    public required int SequenceNumber { get; init; }
    public required string ConnectivityState { get; init; }
    public required DeviceStatusDto Device { get; init; }
    public required FccHealthStatusDto FccHealth { get; init; }
    public required BufferStatusDto Buffer { get; init; }
    public required SyncStatusDto Sync { get; init; }
    public required ErrorCountsDto ErrorCounts { get; init; }
}

public sealed record DeviceStatusDto
{
    public required int BatteryPercent { get; init; }
    public required bool IsCharging { get; init; }
    public required int StorageFreeMb { get; init; }
    public required int StorageTotalMb { get; init; }
    public required int MemoryFreeMb { get; init; }
    public required int MemoryTotalMb { get; init; }
    public required string AppVersion { get; init; }
    public required int AppUptimeSeconds { get; init; }
    public required string OsVersion { get; init; }
    public required string DeviceModel { get; init; }
}

public sealed record FccHealthStatusDto
{
    public required bool IsReachable { get; init; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; init; }
    public int? HeartbeatAgeSeconds { get; init; }
    public required string FccVendor { get; init; }
    public required string FccHost { get; init; }
    public required int FccPort { get; init; }
    public required int ConsecutiveHeartbeatFailures { get; init; }
}

public sealed record BufferStatusDto
{
    public required int TotalRecords { get; init; }
    public required int PendingUploadCount { get; init; }
    public required int SyncedCount { get; init; }
    public required int SyncedToOdooCount { get; init; }
    public required int FailedCount { get; init; }
    public DateTimeOffset? OldestPendingAtUtc { get; init; }
    public required int BufferSizeMb { get; init; }
}

public sealed record SyncStatusDto
{
    public DateTimeOffset? LastSyncAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; init; }
    public int? SyncLagSeconds { get; init; }
    public DateTimeOffset? LastStatusPollUtc { get; init; }
    public DateTimeOffset? LastConfigPullUtc { get; init; }
    public string? ConfigVersion { get; init; }
    public required int UploadBatchSize { get; init; }
}

public sealed record ErrorCountsDto
{
    public required int FccConnectionErrors { get; init; }
    public required int CloudUploadErrors { get; init; }
    public required int CloudAuthErrors { get; init; }
    public required int LocalApiErrors { get; init; }
    public required int BufferWriteErrors { get; init; }
    public required int AdapterNormalizationErrors { get; init; }
    public required int PreAuthErrors { get; init; }
}

public sealed record AgentAuditEventDto
{
    public required Guid Id { get; init; }
    public required Guid DeviceId { get; init; }
    public required string EventType { get; init; }
    public required string Description { get; init; }
    public string? PreviousState { get; init; }
    public string? NewState { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public JsonElement? Metadata { get; init; }
}
