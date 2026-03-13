using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Interfaces;

namespace FccMiddleware.Domain.Entities;

/// <summary>
/// Latest telemetry snapshot reported by an Edge Agent device.
/// Stores a compact detail payload plus indexed summary fields for portal dashboards.
/// </summary>
public class AgentTelemetrySnapshot : ITenantScoped
{
    public Guid DeviceId { get; set; }
    public Guid LegalEntityId { get; set; }
    public string SiteCode { get; set; } = null!;
    public DateTimeOffset ReportedAtUtc { get; set; }
    public ConnectivityState ConnectivityState { get; set; }
    public string PayloadJson { get; set; } = null!;
    public int BatteryPercent { get; set; }
    public bool IsCharging { get; set; }
    public int PendingUploadCount { get; set; }
    public int? SyncLagSeconds { get; set; }
    public DateTimeOffset? LastHeartbeatAtUtc { get; set; }
    public int? HeartbeatAgeSeconds { get; set; }
    public FccVendor FccVendor { get; set; }
    public string FccHost { get; set; } = null!;
    public int FccPort { get; set; }
    public int ConsecutiveHeartbeatFailures { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
