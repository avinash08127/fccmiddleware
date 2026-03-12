using FccDesktopAgent.Core.Adapter.Common;

namespace FccDesktopAgent.Core.Config;

/// <summary>
/// Runtime configuration for the Desktop Edge Agent.
/// Received from cloud and applied at runtime (hot-reload where possible).
/// </summary>
public sealed class AgentConfiguration
{
    /// <summary>Agent's unique device identifier (UUID v4).</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Site identifier this agent is registered to.</summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>FCC base URL over station LAN (e.g. http://192.168.1.100:8080).</summary>
    public string FccBaseUrl { get; set; } = string.Empty;

    /// <summary>Cloud backend base URL.</summary>
    public string CloudBaseUrl { get; set; } = string.Empty;

    /// <summary>FCC polling interval in seconds.</summary>
    public int FccPollIntervalSeconds { get; set; } = 30;

    /// <summary>Cloud sync interval in seconds.</summary>
    public int CloudSyncIntervalSeconds { get; set; } = 60;

    /// <summary>Telemetry report interval in seconds.</summary>
    public int TelemetryIntervalSeconds { get; set; } = 300;

    /// <summary>Maximum transactions to upload per batch.</summary>
    public int UploadBatchSize { get; set; } = 50;

    /// <summary>Ingestion mode controlling how transactions flow through the system.</summary>
    public IngestionMode IngestionMode { get; set; } = IngestionMode.Relay;

    /// <summary>Local REST API port (default 8585).</summary>
    public int LocalApiPort { get; set; } = 8585;

    /// <summary>Whether auto-update is enabled. Can be disabled via config.</summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>URL pointing to the Velopack releases endpoint (GitHub Releases or cloud storage).</summary>
    public string UpdateUrl { get; set; } = string.Empty;

    /// <summary>Cleanup worker interval in hours (default 24).</summary>
    public int CleanupIntervalHours { get; set; } = 24;

    /// <summary>Retention period in days for synced transactions, terminal pre-auths, and audit log entries.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>FCC hardware protocol vendor. Determines which adapter is created.</summary>
    public FccVendor FccVendor { get; set; } = FccVendor.Doms;

    /// <summary>
    /// FCC API key for LAN authentication.
    /// TODO: Replace with ICredentialStore when DEA security task is implemented.
    /// </summary>
    public string FccApiKey { get; set; } = string.Empty;

    /// <summary>Timeout in seconds for FCC pre-auth calls (default 30s per architecture spec).</summary>
    public int PreAuthTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Duration in minutes before an authorized pre-auth expires if not completed (default 5 min).
    /// Overridden by the FCC-provided ExpiresAt when the adapter returns one.
    /// </summary>
    public int PreAuthExpiryMinutes { get; set; } = 5;

    /// <summary>
    /// Interval in seconds between each connectivity probe cycle (internet + FCC probes run in parallel).
    /// ±20% jitter is applied at runtime to prevent synchronized bursts across devices. Default 30s.
    /// </summary>
    public int ConnectivityProbeIntervalSeconds { get; set; } = 30;
}
