using System.Text.Json.Serialization;

namespace FccDesktopAgent.Core.Sync.Models;

/// <summary>
/// Telemetry payload sent to <c>POST /api/v1/agent/telemetry</c>.
/// Matches <c>schemas/canonical/telemetry-payload.schema.json</c>.
/// One full snapshot per reporting interval per device.
/// </summary>
public sealed class TelemetryPayload
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("deviceId")]
    public string DeviceId { get; init; } = string.Empty;

    [JsonPropertyName("siteCode")]
    public string SiteCode { get; init; } = string.Empty;

    [JsonPropertyName("legalEntityId")]
    public string LegalEntityId { get; init; } = string.Empty;

    [JsonPropertyName("reportedAtUtc")]
    public DateTimeOffset ReportedAtUtc { get; init; }

    [JsonPropertyName("sequenceNumber")]
    public int SequenceNumber { get; init; }

    [JsonPropertyName("connectivityState")]
    public string ConnectivityState { get; init; } = string.Empty;

    [JsonPropertyName("device")]
    public TelemetryDeviceStatus Device { get; init; } = new();

    [JsonPropertyName("fccHealth")]
    public TelemetryFccHealth FccHealth { get; init; } = new();

    [JsonPropertyName("buffer")]
    public TelemetryBufferStatus Buffer { get; init; } = new();

    [JsonPropertyName("sync")]
    public TelemetrySyncStatus Sync { get; init; } = new();

    [JsonPropertyName("errorCounts")]
    public TelemetryErrorCounts ErrorCounts { get; init; } = new();
}

/// <summary>Device-level health metrics.</summary>
public sealed class TelemetryDeviceStatus
{
    [JsonPropertyName("batteryPercent")]
    public int BatteryPercent { get; init; }

    [JsonPropertyName("isCharging")]
    public bool IsCharging { get; init; }

    [JsonPropertyName("storageFreeMb")]
    public int StorageFreeMb { get; init; }

    [JsonPropertyName("storageTotalMb")]
    public int StorageTotalMb { get; init; }

    [JsonPropertyName("memoryFreeMb")]
    public int MemoryFreeMb { get; init; }

    [JsonPropertyName("memoryTotalMb")]
    public int MemoryTotalMb { get; init; }

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; init; } = "0.0.0";

    [JsonPropertyName("appUptimeSeconds")]
    public int AppUptimeSeconds { get; init; }

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; init; } = string.Empty;

    [JsonPropertyName("deviceModel")]
    public string DeviceModel { get; init; } = string.Empty;
}

/// <summary>FCC connectivity and health status.</summary>
public sealed class TelemetryFccHealth
{
    [JsonPropertyName("isReachable")]
    public bool IsReachable { get; init; }

    [JsonPropertyName("lastHeartbeatAtUtc")]
    public DateTimeOffset? LastHeartbeatAtUtc { get; init; }

    [JsonPropertyName("heartbeatAgeSeconds")]
    public int? HeartbeatAgeSeconds { get; init; }

    [JsonPropertyName("fccVendor")]
    public string FccVendor { get; init; } = string.Empty;

    [JsonPropertyName("fccHost")]
    public string FccHost { get; init; } = string.Empty;

    [JsonPropertyName("fccPort")]
    public int FccPort { get; init; }

    [JsonPropertyName("consecutiveHeartbeatFailures")]
    public int ConsecutiveHeartbeatFailures { get; init; }
}

/// <summary>Local SQLite buffer status.</summary>
public sealed class TelemetryBufferStatus
{
    [JsonPropertyName("totalRecords")]
    public int TotalRecords { get; init; }

    [JsonPropertyName("pendingUploadCount")]
    public int PendingUploadCount { get; init; }

    [JsonPropertyName("syncedCount")]
    public int SyncedCount { get; init; }

    [JsonPropertyName("syncedToOdooCount")]
    public int SyncedToOdooCount { get; init; }

    [JsonPropertyName("failedCount")]
    public int FailedCount { get; init; }

    [JsonPropertyName("oldestPendingAtUtc")]
    public DateTimeOffset? OldestPendingAtUtc { get; init; }

    [JsonPropertyName("bufferSizeMb")]
    public int BufferSizeMb { get; init; }
}

/// <summary>Cloud synchronization status.</summary>
public sealed class TelemetrySyncStatus
{
    [JsonPropertyName("lastSyncAttemptUtc")]
    public DateTimeOffset? LastSyncAttemptUtc { get; init; }

    [JsonPropertyName("lastSuccessfulSyncUtc")]
    public DateTimeOffset? LastSuccessfulSyncUtc { get; init; }

    [JsonPropertyName("syncLagSeconds")]
    public int? SyncLagSeconds { get; init; }

    [JsonPropertyName("lastStatusPollUtc")]
    public DateTimeOffset? LastStatusPollUtc { get; init; }

    [JsonPropertyName("lastConfigPullUtc")]
    public DateTimeOffset? LastConfigPullUtc { get; init; }

    [JsonPropertyName("configVersion")]
    public string? ConfigVersion { get; init; }

    [JsonPropertyName("uploadBatchSize")]
    public int UploadBatchSize { get; init; }
}

/// <summary>Rolling error counts since last successful telemetry submission.</summary>
public sealed class TelemetryErrorCounts
{
    [JsonPropertyName("fccConnectionErrors")]
    public int FccConnectionErrors { get; init; }

    [JsonPropertyName("cloudUploadErrors")]
    public int CloudUploadErrors { get; init; }

    [JsonPropertyName("cloudAuthErrors")]
    public int CloudAuthErrors { get; init; }

    [JsonPropertyName("localApiErrors")]
    public int LocalApiErrors { get; init; }

    [JsonPropertyName("bufferWriteErrors")]
    public int BufferWriteErrors { get; init; }

    [JsonPropertyName("adapterNormalizationErrors")]
    public int AdapterNormalizationErrors { get; init; }

    [JsonPropertyName("preAuthErrors")]
    public int PreAuthErrors { get; init; }
}
