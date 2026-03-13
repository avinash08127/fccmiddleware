using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Security;

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

    /// <summary>Legal entity ID from registration or site config.</summary>
    public string LegalEntityId { get; set; } = string.Empty;

    /// <summary>FCC base URL over station LAN (e.g. http://192.168.1.100:8080).</summary>
    public string FccBaseUrl { get; set; } = string.Empty;

    /// <summary>Cloud backend base URL.</summary>
    public string CloudBaseUrl { get; set; } = string.Empty;

    /// <summary>Cloud environment key (e.g. "PRODUCTION", "STAGING"). Null for legacy/custom URL registrations.</summary>
    public string? Environment { get; set; }

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
    /// Stored in <see cref="ICredentialStore"/> at rest — this in-memory copy is populated
    /// on startup from the credential store. Never persisted to appsettings or SQLite.
    /// </summary>
    [SensitiveData]
    public string FccApiKey { get; set; } = string.Empty;

    /// <summary>Timeout in seconds for FCC pre-auth calls (default 30s per architecture spec).</summary>
    public int PreAuthTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// PA-P03: Maximum pre-auth records processed per expiry check cycle (default 50).
    /// Bounds memory use and loop duration when a large backlog accumulates during an FCC outage.
    /// </summary>
    public int PreAuthExpiryBatchSize { get; set; } = 50;

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

    /// <summary>Config poll interval in seconds (range 30–3600, default 60).</summary>
    public int ConfigPollIntervalSeconds { get; set; } = 60;

    /// <summary>SYNCED_TO_ODOO status poll interval in seconds (default 300 = 5 minutes).</summary>
    public int StatusPollIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Petronite webhook listener port (default 8090). The local HTTP server that receives
    /// transaction callbacks from the Petronite bot. Cloud config takes precedence if set.
    /// </summary>
    public int PetroniteWebhookListenerPort { get; set; } = 8090;

    // ── Certificate Pinning ────────────────────────────────────────────────

    /// <summary>
    /// T-DSK-015: Additional SPKI SHA-256 Base64 hashes to trust alongside the compiled-in
    /// bootstrap pins. Loaded from cloud config or appsettings to enable emergency pin rotation
    /// without a software update. Null or empty means only bootstrap pins are used.
    /// </summary>
    public List<string>? AdditionalCertificatePins { get; set; }

    // ── WebSocket Server ──────────────────────────────────────────────────

    /// <summary>Whether the Odoo backward-compat WebSocket server is enabled.</summary>
    public bool WebSocketEnabled { get; set; }

    /// <summary>Port the WebSocket server listens on (legacy default: 8443).</summary>
    public int WebSocketPort { get; set; } = 8443;

    /// <summary>Maximum concurrent WebSocket connections.</summary>
    public int WebSocketMaxConnections { get; set; } = 10;

    /// <summary>Interval in seconds for per-connection pump status broadcasts.</summary>
    public int WebSocketPumpStatusBroadcastIntervalSeconds { get; set; } = 3;

    // ── Local Override Properties ───────────────────────────────────────────

    /// <summary>
    /// Local FCC host override. When set, takes precedence over the cloud-delivered host address.
    /// Populated from <see cref="LocalOverrideManager"/> at runtime.
    /// </summary>
    public string? FccHostOverride { get; set; }

    /// <summary>
    /// Local FCC port override. When set, takes precedence over the cloud-delivered port.
    /// Populated from <see cref="LocalOverrideManager"/> at runtime.
    /// </summary>
    public int? FccPortOverride { get; set; }

    /// <summary>
    /// Returns the effective FCC base URL, applying local overrides when present.
    /// Falls back to <see cref="FccBaseUrl"/> when no overrides are active.
    /// </summary>
    public string GetEffectiveFccBaseUrl(LocalOverrideManager? overrides = null)
    {
        var host = overrides?.FccHost ?? FccHostOverride;
        var port = overrides?.FccPort ?? FccPortOverride;

        if (!string.IsNullOrWhiteSpace(host) && port is > 0)
            return $"http://{host}:{port}";

        if (!string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(FccBaseUrl))
        {
            // Override host only — extract port from existing base URL
            if (Uri.TryCreate(FccBaseUrl, UriKind.Absolute, out var existing))
                return $"http://{host}:{existing.Port}";
        }

        if (port is > 0 && !string.IsNullOrWhiteSpace(FccBaseUrl))
        {
            // Override port only — extract host from existing base URL
            if (Uri.TryCreate(FccBaseUrl, UriKind.Absolute, out var existing))
                return $"http://{existing.Host}:{port}";
        }

        return FccBaseUrl;
    }
}
