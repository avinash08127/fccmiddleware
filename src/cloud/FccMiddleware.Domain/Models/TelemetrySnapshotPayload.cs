using System.Text.Json;

namespace FccMiddleware.Domain.Models;

/// <summary>
/// Compact telemetry snapshot payload stored alongside indexed summary columns.
/// Excludes fields already persisted individually on <see cref="Entities.AgentTelemetrySnapshot"/>.
/// </summary>
public sealed class TelemetrySnapshotPayload
{
    public const string SupplementalFormat = "supplemental-v1";

    public string Format { get; set; } = SupplementalFormat;
    public string SchemaVersion { get; set; } = "1.0";
    public int SequenceNumber { get; set; }
    public SnapshotDeviceStatus Device { get; set; } = null!;
    public SnapshotFccHealthStatus FccHealth { get; set; } = null!;
    public SnapshotBufferStatus Buffer { get; set; } = null!;
    public SnapshotSyncStatus Sync { get; set; } = null!;
    public ErrorCounts ErrorCounts { get; set; } = null!;

    public static TelemetrySnapshotPayload FromTelemetry(TelemetryPayload payload) =>
        new()
        {
            SchemaVersion = payload.SchemaVersion,
            SequenceNumber = payload.SequenceNumber,
            Device = new SnapshotDeviceStatus
            {
                StorageFreeMb = payload.Device.StorageFreeMb,
                StorageTotalMb = payload.Device.StorageTotalMb,
                MemoryFreeMb = payload.Device.MemoryFreeMb,
                MemoryTotalMb = payload.Device.MemoryTotalMb,
                AppVersion = payload.Device.AppVersion,
                AppUptimeSeconds = payload.Device.AppUptimeSeconds,
                OsVersion = payload.Device.OsVersion,
                DeviceModel = payload.Device.DeviceModel
            },
            FccHealth = new SnapshotFccHealthStatus
            {
                IsReachable = payload.FccHealth.IsReachable
            },
            Buffer = new SnapshotBufferStatus
            {
                TotalRecords = payload.Buffer.TotalRecords,
                SyncedCount = payload.Buffer.SyncedCount,
                SyncedToOdooCount = payload.Buffer.SyncedToOdooCount,
                FailedCount = payload.Buffer.FailedCount,
                OldestPendingAtUtc = payload.Buffer.OldestPendingAtUtc,
                BufferSizeMb = payload.Buffer.BufferSizeMb
            },
            Sync = new SnapshotSyncStatus
            {
                LastSyncAttemptUtc = payload.Sync.LastSyncAttemptUtc,
                LastSuccessfulSyncUtc = payload.Sync.LastSuccessfulSyncUtc,
                LastStatusPollUtc = payload.Sync.LastStatusPollUtc,
                LastConfigPullUtc = payload.Sync.LastConfigPullUtc,
                ConfigVersion = payload.Sync.ConfigVersion,
                UploadBatchSize = payload.Sync.UploadBatchSize
            },
            ErrorCounts = new ErrorCounts
            {
                FccConnectionErrors = payload.ErrorCounts.FccConnectionErrors,
                CloudUploadErrors = payload.ErrorCounts.CloudUploadErrors,
                CloudAuthErrors = payload.ErrorCounts.CloudAuthErrors,
                LocalApiErrors = payload.ErrorCounts.LocalApiErrors,
                BufferWriteErrors = payload.ErrorCounts.BufferWriteErrors,
                AdapterNormalizationErrors = payload.ErrorCounts.AdapterNormalizationErrors,
                PreAuthErrors = payload.ErrorCounts.PreAuthErrors
            }
        };

    public static bool TryDeserialize(string json, JsonSerializerOptions options, out TelemetrySnapshotPayload? payload)
    {
        payload = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("format", out var formatElement)
                || !string.Equals(formatElement.GetString(), SupplementalFormat, StringComparison.Ordinal))
            {
                return false;
            }

            payload = JsonSerializer.Deserialize<TelemetrySnapshotPayload>(json, options);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryReadSequenceNumber(string json, JsonSerializerOptions options, out int sequenceNumber)
    {
        sequenceNumber = default;

        if (TryDeserialize(json, options, out var snapshotPayload))
        {
            sequenceNumber = snapshotPayload!.SequenceNumber;
            return true;
        }

        try
        {
            var legacyPayload = JsonSerializer.Deserialize<TelemetryPayload>(json, options);
            if (legacyPayload is null)
            {
                return false;
            }

            sequenceNumber = legacyPayload.SequenceNumber;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class SnapshotDeviceStatus
{
    public int StorageFreeMb { get; set; }
    public int StorageTotalMb { get; set; }
    public int MemoryFreeMb { get; set; }
    public int MemoryTotalMb { get; set; }
    public string AppVersion { get; set; } = null!;
    public int AppUptimeSeconds { get; set; }
    public string OsVersion { get; set; } = null!;
    public string DeviceModel { get; set; } = null!;
}

public sealed class SnapshotFccHealthStatus
{
    public bool IsReachable { get; set; }
}

public sealed class SnapshotBufferStatus
{
    public int TotalRecords { get; set; }
    public int SyncedCount { get; set; }
    public int SyncedToOdooCount { get; set; }
    public int FailedCount { get; set; }
    public DateTimeOffset? OldestPendingAtUtc { get; set; }
    public int BufferSizeMb { get; set; }
}

public sealed class SnapshotSyncStatus
{
    public DateTimeOffset? LastSyncAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessfulSyncUtc { get; set; }
    public DateTimeOffset? LastStatusPollUtc { get; set; }
    public DateTimeOffset? LastConfigPullUtc { get; set; }
    public string? ConfigVersion { get; set; }
    public int UploadBatchSize { get; set; }
}
