using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Sync.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Collects device health telemetry and sends it to the cloud backend via
/// <c>POST /api/v1/agent/telemetry</c>.
///
/// Architecture rules satisfied:
/// - Rule #10: No independent timer loop — invoked by CadenceController on telemetry ticks.
/// - Fire-and-forget: if send fails, the report is silently skipped (no telemetry buffering).
/// - Only sends when internet is available (gated by CadenceController connectivity check).
/// - Error counts are reset to zero only after a successful submission.
/// </summary>
public sealed class TelemetryReporter : ITelemetryReporter
{
    private const string TelemetryPath = "/api/v1/agent/telemetry";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly IConnectivityMonitor _connectivity;
    private readonly AuthenticatedCloudRequestHandler _authHandler;
    private readonly IRegistrationManager _registrationManager;
    private readonly IErrorCountTracker _errorTracker;
    private readonly ILogger<TelemetryReporter> _logger;

    // In-memory monotonic sequence number. Starts at 1 per process lifetime.
    private int _sequenceNumber;

    // Process start time — cached for uptime calculation.
    private static readonly DateTimeOffset ProcessStartTime = GetProcessStartTime();

    public TelemetryReporter(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpFactory,
        IOptions<AgentConfiguration> config,
        IConnectivityMonitor connectivity,
        AuthenticatedCloudRequestHandler authHandler,
        IRegistrationManager registrationManager,
        IErrorCountTracker errorTracker,
        ILogger<TelemetryReporter> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _config = config;
        _connectivity = connectivity;
        _authHandler = authHandler;
        _registrationManager = registrationManager;
        _errorTracker = errorTracker;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ReportAsync(CancellationToken ct)
    {
        // T-DSK-013: Check centralized decommission flag — TelemetryReporter was missing
        // this check, causing it to keep sending telemetry after other workers had stopped.
        if (_registrationManager.IsDecommissioned)
        {
            _logger.LogDebug("Telemetry skipped: device is decommissioned");
            return false;
        }

        var config = _config.Value;

        // Peek at error counts before building the payload. Only reset after successful send.
        var errorSnapshot = _errorTracker.Peek();

        TelemetryPayload payload;
        try
        {
            payload = await BuildPayloadAsync(config, errorSnapshot, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to collect telemetry data");
            return false;
        }

        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{TelemetryPath}";

        // T-DSK-010: Delegate auth flow to the shared handler.
        var result = await _authHandler.ExecuteAsync<bool>(
            (token, innerCt) => SendTelemetryAsync(payload, url, token, innerCt),
            "telemetry", ct);

        if (!result.IsSuccess)
            return false;

        // Success — now atomically consume the error counts so they reset.
        _errorTracker.TakeSnapshot();

        _logger.LogDebug(
            "Telemetry report #{Sequence} sent successfully", payload.SequenceNumber);
        return true;
    }

    // ── HTTP send ──────────────────────────────────────────────────────────────

    private async Task<bool> SendTelemetryAsync(
        TelemetryPayload payload, string url, string token, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("cloud");

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(payload);

        var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("DEVICE_DECOMMISSIONED", StringComparison.OrdinalIgnoreCase))
                throw new DeviceDecommissionedException(body);
            throw new HttpRequestException($"403 Forbidden: {body}");
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    // ── Payload assembly ──────────────────────────────────────────────────────

    private async Task<TelemetryPayload> BuildPayloadAsync(
        AgentConfiguration config,
        ErrorCountSnapshot errors,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var sequence = Interlocked.Increment(ref _sequenceNumber);
        var snapshot = _connectivity.Current;

        // Buffer stats require a scoped DbContext.
        BufferStats bufferStats;
        SyncStateRecord? syncState;
        using (var scope = _scopeFactory.CreateScope())
        {
            var bufferManager = scope.ServiceProvider.GetRequiredService<TransactionBufferManager>();
            bufferStats = await bufferManager.GetBufferStatsAsync(ct);

            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
            syncState = await db.SyncStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == 1, ct);
        }

        return new TelemetryPayload
        {
            SchemaVersion = "1.0",
            DeviceId = config.DeviceId,
            SiteCode = config.SiteId,
            LegalEntityId = !string.IsNullOrWhiteSpace(config.LegalEntityId) ? config.LegalEntityId : config.SiteId,
            ReportedAtUtc = now,
            SequenceNumber = sequence,
            ConnectivityState = MapConnectivityState(snapshot.State),
            Device = CollectDeviceStatus(),
            FccHealth = CollectFccHealth(config, snapshot),
            Buffer = CollectBufferStatus(bufferStats),
            Sync = CollectSyncStatus(config, syncState, bufferStats, now),
            ErrorCounts = MapErrorCounts(errors),
        };
    }

    // ── Device status ─────────────────────────────────────────────────────────

    private static TelemetryDeviceStatus CollectDeviceStatus()
    {
        var process = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();

        // Storage: free/total for the drive hosting the data directory.
        int storageFreeMb = 0;
        int storageTotalMb = 0;
        try
        {
            var dbPath = AgentDataDirectory.GetDatabasePath();
            var driveRoot = Path.GetPathRoot(dbPath);
            if (driveRoot is not null)
            {
                var drive = new DriveInfo(driveRoot);
                if (drive.IsReady)
                {
                    storageFreeMb = (int)(drive.AvailableFreeSpace / (1024 * 1024));
                    storageTotalMb = (int)(drive.TotalSize / (1024 * 1024));
                }
            }
        }
        catch
        {
            // DriveInfo may not work on all platforms; fall back to 0.
        }

        // Memory: total available vs current working set.
        var totalMemoryMb = (int)(gcInfo.TotalAvailableMemoryBytes / (1024 * 1024));
        var workingSetMb = (int)(process.WorkingSet64 / (1024 * 1024));
        var freeMemoryMb = Math.Max(0, totalMemoryMb - workingSetMb);

        // App version from entry assembly.
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        var appVersion = version is not null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";

        // Uptime since process start.
        var uptimeSeconds = (int)(DateTimeOffset.UtcNow - ProcessStartTime).TotalSeconds;

        return new TelemetryDeviceStatus
        {
            // Desktop is always powered — report 100% battery, charging.
            BatteryPercent = 100,
            IsCharging = true,
            StorageFreeMb = storageFreeMb,
            StorageTotalMb = storageTotalMb,
            MemoryFreeMb = freeMemoryMb,
            MemoryTotalMb = totalMemoryMb,
            AppVersion = appVersion,
            AppUptimeSeconds = uptimeSeconds,
            OsVersion = TruncateString(RuntimeInformation.OSDescription, 20),
            DeviceModel = TruncateString(Environment.MachineName, 100),
        };
    }

    // ── FCC health ────────────────────────────────────────────────────────────

    private TelemetryFccHealth CollectFccHealth(
        AgentConfiguration config,
        ConnectivitySnapshot snapshot)
    {
        var lastHeartbeat = _connectivity.LastFccSuccessAtUtc;
        int? heartbeatAge = lastHeartbeat.HasValue
            ? (int)(DateTimeOffset.UtcNow - lastHeartbeat.Value).TotalSeconds
            : null;

        // Parse host and port from FCC base URL.
        ParseFccHostPort(config.FccBaseUrl, out var host, out var port);

        return new TelemetryFccHealth
        {
            IsReachable = snapshot.IsFccUp,
            LastHeartbeatAtUtc = lastHeartbeat,
            HeartbeatAgeSeconds = heartbeatAge,
            FccVendor = config.FccVendor.ToString().ToUpperInvariant(),
            FccHost = host,
            FccPort = port,
            ConsecutiveHeartbeatFailures = _connectivity.FccConsecutiveFailures,
        };
    }

    // ── Buffer status ─────────────────────────────────────────────────────────

    private static TelemetryBufferStatus CollectBufferStatus(BufferStats stats)
    {
        int bufferSizeMb = 0;
        try
        {
            var dbPath = AgentDataDirectory.GetDatabasePath();
            if (File.Exists(dbPath))
                bufferSizeMb = (int)(new FileInfo(dbPath).Length / (1024 * 1024));
        }
        catch
        {
            // File access may fail; fall back to 0.
        }

        return new TelemetryBufferStatus
        {
            TotalRecords = stats.Total,
            PendingUploadCount = stats.Pending,
            SyncedCount = stats.Uploaded,
            SyncedToOdooCount = stats.SyncedToOdoo,
            FailedCount = 0, // Failed records stay as Pending; no separate Failed status.
            OldestPendingAtUtc = stats.OldestPendingAtUtc,
            BufferSizeMb = bufferSizeMb,
        };
    }

    // ── Sync status ───────────────────────────────────────────────────────────

    private static TelemetrySyncStatus CollectSyncStatus(
        AgentConfiguration config,
        SyncStateRecord? syncState,
        BufferStats bufferStats,
        DateTimeOffset now)
    {
        int? syncLagSeconds = null;
        if (bufferStats.OldestPendingAtUtc.HasValue)
            syncLagSeconds = (int)(now - bufferStats.OldestPendingAtUtc.Value).TotalSeconds;

        return new TelemetrySyncStatus
        {
            LastSyncAttemptUtc = syncState?.LastUploadAt,
            LastSuccessfulSyncUtc = syncState?.LastUploadAt, // Currently same field; split when tracked separately.
            SyncLagSeconds = syncLagSeconds,
            LastStatusPollUtc = syncState?.LastStatusSyncAt,
            LastConfigPullUtc = syncState?.LastConfigSyncAt,
            ConfigVersion = syncState?.ConfigVersion,
            UploadBatchSize = config.UploadBatchSize,
        };
    }

    // ── Error counts mapping ──────────────────────────────────────────────────

    private static TelemetryErrorCounts MapErrorCounts(ErrorCountSnapshot errors) => new()
    {
        FccConnectionErrors = errors.FccConnectionErrors,
        CloudUploadErrors = errors.CloudUploadErrors,
        CloudAuthErrors = errors.CloudAuthErrors,
        LocalApiErrors = errors.LocalApiErrors,
        BufferWriteErrors = errors.BufferWriteErrors,
        AdapterNormalizationErrors = errors.AdapterNormalizationErrors,
        PreAuthErrors = errors.PreAuthErrors,
    };

    // ── Connectivity state mapping ────────────────────────────────────────────

    private static string MapConnectivityState(ConnectivityState state) => state switch
    {
        Connectivity.ConnectivityState.FullyOnline => "FULLY_ONLINE",
        Connectivity.ConnectivityState.InternetDown => "INTERNET_DOWN",
        Connectivity.ConnectivityState.FccUnreachable => "FCC_UNREACHABLE",
        Connectivity.ConnectivityState.FullyOffline => "FULLY_OFFLINE",
        _ => "FULLY_OFFLINE",
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ParseFccHostPort(string fccBaseUrl, out string host, out int port)
    {
        host = "unknown";
        port = 0;

        if (string.IsNullOrWhiteSpace(fccBaseUrl))
            return;

        try
        {
            var uri = new Uri(fccBaseUrl);
            host = uri.Host;
            port = uri.Port;
        }
        catch (UriFormatException)
        {
            // Malformed URL — return defaults.
        }
    }

    private static string TruncateString(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static DateTimeOffset GetProcessStartTime()
    {
        try
        {
            return Process.GetCurrentProcess().StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
