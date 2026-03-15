using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Contracts.Telemetry;

public sealed class SubmitTelemetryRequest
{
    [Required]
    [RegularExpression("^1\\.0$")]
    public string SchemaVersion { get; set; } = "1.0";

    [Required]
    public Guid DeviceId { get; set; }

    [Required]
    [StringLength(50, MinimumLength = 1)]
    [RegularExpression("^[a-zA-Z0-9-]+$")]
    public string SiteCode { get; set; } = null!;

    [Required]
    public Guid LegalEntityId { get; set; }

    [Required]
    public DateTimeOffset ReportedAtUtc { get; set; }

    [Range(1, int.MaxValue)]
    public int SequenceNumber { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConnectivityState ConnectivityState { get; set; }

    [Required]
    public SubmitTelemetryDeviceStatusRequest Device { get; set; } = null!;

    [Required]
    public SubmitTelemetryFccHealthRequest FccHealth { get; set; } = null!;

    [Required]
    public SubmitTelemetryBufferStatusRequest Buffer { get; set; } = null!;

    [Required]
    public SubmitTelemetrySyncStatusRequest Sync { get; set; } = null!;

    [Required]
    public SubmitTelemetryErrorCountsRequest ErrorCounts { get; set; } = null!;
}

public sealed class SubmitTelemetryDeviceStatusRequest
{
    [Range(0, 100)]
    public int BatteryPercent { get; set; }

    public bool IsCharging { get; set; }

    [Range(0, int.MaxValue)]
    public int StorageFreeMb { get; set; }

    [Range(1, int.MaxValue)]
    public int StorageTotalMb { get; set; }

    [Range(0, int.MaxValue)]
    public int MemoryFreeMb { get; set; }

    [Range(1, int.MaxValue)]
    public int MemoryTotalMb { get; set; }

    [Required]
    [RegularExpression("^\\d+\\.\\d+\\.\\d+$")]
    public string AppVersion { get; set; } = null!;

    [Range(0, int.MaxValue)]
    public int AppUptimeSeconds { get; set; }

    [Required]
    [StringLength(20, MinimumLength = 1)]
    public string OsVersion { get; set; } = null!;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string DeviceModel { get; set; } = null!;
}

public sealed class SubmitTelemetryFccHealthRequest
{
    public bool IsReachable { get; set; }

    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }

    [Range(0, int.MaxValue)]
    public int? HeartbeatAgeSeconds { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FccVendor FccVendor { get; set; }

    [Required]
    [MinLength(1)]
    public string FccHost { get; set; } = null!;

    [Range(1, 65535)]
    public int FccPort { get; set; }

    [Range(0, int.MaxValue)]
    public int ConsecutiveHeartbeatFailures { get; set; }
}

public sealed class SubmitTelemetryBufferStatusRequest
{
    [Range(0, int.MaxValue)]
    public int TotalRecords { get; set; }

    [Range(0, int.MaxValue)]
    public int PendingUploadCount { get; set; }

    [Range(0, int.MaxValue)]
    public int SyncedCount { get; set; }

    [Range(0, int.MaxValue)]
    public int SyncedToOdooCount { get; set; }

    [Range(0, int.MaxValue)]
    public int FailedCount { get; set; }

    public DateTimeOffset? OldestPendingAtUtc { get; set; }

    [Range(0, int.MaxValue)]
    public int BufferSizeMb { get; set; }

    /// <summary>Count of records permanently failed after max upload retries (DEAD_LETTER status).</summary>
    [Range(0, int.MaxValue)]
    public int DeadLetterCount { get; set; }

    /// <summary>Count of records archived after successful sync lifecycle completion.</summary>
    [Range(0, int.MaxValue)]
    public int ArchivedCount { get; set; }

    /// <summary>Count of records pending fiscalization.</summary>
    [Range(0, int.MaxValue)]
    public int FiscalPendingCount { get; set; }

    /// <summary>Count of records permanently failed fiscalization.</summary>
    [Range(0, int.MaxValue)]
    public int FiscalDeadLetterCount { get; set; }
}

public sealed class SubmitTelemetrySyncStatusRequest
{
    public DateTimeOffset? LastSyncAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }

    [Range(0, int.MaxValue)]
    public int? SyncLagSeconds { get; set; }

    public DateTimeOffset? LastStatusPollUtc { get; set; }
    public DateTimeOffset? LastConfigPullUtc { get; set; }

    public string? ConfigVersion { get; set; }

    [Range(0, int.MaxValue)]
    public int UploadBatchSize { get; set; }
}

public sealed class SubmitTelemetryErrorCountsRequest
{
    [Range(0, int.MaxValue)]
    public int FccConnectionErrors { get; set; }

    [Range(0, int.MaxValue)]
    public int CloudUploadErrors { get; set; }

    [Range(0, int.MaxValue)]
    public int CloudAuthErrors { get; set; }

    [Range(0, int.MaxValue)]
    public int LocalApiErrors { get; set; }

    [Range(0, int.MaxValue)]
    public int BufferWriteErrors { get; set; }

    [Range(0, int.MaxValue)]
    public int AdapterNormalizationErrors { get; set; }

    [Range(0, int.MaxValue)]
    public int PreAuthErrors { get; set; }
}
