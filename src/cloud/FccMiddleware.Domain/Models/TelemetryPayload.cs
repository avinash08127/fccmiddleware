using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Domain.Models;

/// <summary>
/// Health metrics payload reported by an Edge Agent to the Cloud Backend.
/// Matches telemetry-payload.schema.json v1.0.
/// One payload per reporting interval per device.
/// </summary>
public class TelemetryPayload
{
    public string SchemaVersion { get; set; } = "1.0";
    public Guid DeviceId { get; set; }
    public string SiteCode { get; set; } = null!;
    public Guid LegalEntityId { get; set; }
    public DateTimeOffset ReportedAtUtc { get; set; }

    /// <summary>Monotonically increasing counter per device; starts at 1 after registration.</summary>
    public int SequenceNumber { get; set; }

    public ConnectivityState ConnectivityState { get; set; }
    public DeviceStatus Device { get; set; } = null!;
    public FccHealthStatus FccHealth { get; set; } = null!;
    public BufferStatus Buffer { get; set; } = null!;
    public SyncStatus Sync { get; set; } = null!;
    public ErrorCounts ErrorCounts { get; set; } = null!;
}

/// <summary>Device-level health metrics from the Android HHT.</summary>
public class DeviceStatus
{
    /// <summary>Current battery level (0–100).</summary>
    public int BatteryPercent { get; set; }
    public bool IsCharging { get; set; }
    public int StorageFreeMb { get; set; }
    public int StorageTotalMb { get; set; }
    public int MemoryFreeMb { get; set; }
    public int MemoryTotalMb { get; set; }
    public string AppVersion { get; set; } = null!;
    public int AppUptimeSeconds { get; set; }
    public string OsVersion { get; set; } = null!;
    public string DeviceModel { get; set; } = null!;
}

/// <summary>FCC connectivity and health status.</summary>
public class FccHealthStatus
{
    public bool IsReachable { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public int? HeartbeatAgeSeconds { get; set; }
    public FccVendor FccVendor { get; set; }
    public string FccHost { get; set; } = null!;
    public int FccPort { get; set; }
    public int ConsecutiveHeartbeatFailures { get; set; }
}

/// <summary>Local SQLite buffer status.</summary>
public class BufferStatus
{
    public int TotalRecords { get; set; }
    public int PendingUploadCount { get; set; }
    public int SyncedCount { get; set; }
    public int SyncedToOdooCount { get; set; }
    public int FailedCount { get; set; }
    public DateTimeOffset? OldestPendingAtUtc { get; set; }
    public int BufferSizeMb { get; set; }
}

/// <summary>Cloud synchronization status.</summary>
public class SyncStatus
{
    public DateTimeOffset? LastSyncAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public int? SyncLagSeconds { get; set; }
    public DateTimeOffset? LastStatusPollUtc { get; set; }
    public DateTimeOffset? LastConfigPullUtc { get; set; }
    public string? ConfigVersion { get; set; }
    public int UploadBatchSize { get; set; }
}

/// <summary>
/// Rolling error counts since last successful telemetry submission.
/// All counters reset to 0 after a successful telemetry send.
/// </summary>
public class ErrorCounts
{
    public int FccConnectionErrors { get; set; }
    public int CloudUploadErrors { get; set; }
    public int CloudAuthErrors { get; set; }
    public int LocalApiErrors { get; set; }
    public int BufferWriteErrors { get; set; }
    public int AdapterNormalizationErrors { get; set; }
    public int PreAuthErrors { get; set; }
}
