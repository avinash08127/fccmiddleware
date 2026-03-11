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
}
