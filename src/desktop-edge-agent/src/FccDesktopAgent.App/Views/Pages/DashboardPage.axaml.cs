using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Ingestion;
using FccDesktopAgent.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.App.Views.Pages;

public sealed partial class DashboardPage : UserControl, IDisposable
{
    private readonly IConnectivityMonitor? _connectivity;
    private readonly IServiceProvider? _services;
    private readonly Timer _refreshTimer;
    private static readonly DateTimeOffset ProcessStartTime = GetProcessStartTime();

    // P-DSK-016: Cache brushes to avoid allocating new SolidColorBrush objects on every state change.
    private static readonly SolidColorBrush GreenBrush = new(Color.Parse("#22C55E"));
    private static readonly SolidColorBrush YellowBrush = new(Color.Parse("#EAB308"));
    private static readonly SolidColorBrush RedBrush = new(Color.Parse("#EF4444"));
    private static readonly SolidColorBrush GrayBrush = new(Color.Parse("#888888"));

    public DashboardPage()
    {
        InitializeComponent();

        _services = AgentAppContext.ServiceProvider;
        _connectivity = _services?.GetService<IConnectivityMonitor>();

        if (_connectivity is not null)
        {
            _connectivity.StateChanged += OnConnectivityChanged;
            UpdateConnectivityDisplay(_connectivity.Current);
        }

        // Populate static device info once
        PopulateDeviceInfo();

        // P-DSK-015/023: Increased from 5s to 15s to reduce DB query frequency
        _refreshTimer = new Timer(_ => _ = RefreshAllAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    // P-DSK-023: Pause the timer when the page is not visible to avoid duplicate
    // DB polling alongside MainWindowViewModel's status bar timer.
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _refreshTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _refreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
        base.OnDetachedFromVisualTree(e);
    }

    // ── Connectivity ──────────────────────────────────────────────────────────

    private void OnConnectivityChanged(object? sender, ConnectivitySnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => UpdateConnectivityDisplay(snapshot));
    }

    private void UpdateConnectivityDisplay(ConnectivitySnapshot snapshot)
    {
        var (text, brush, hint) = snapshot.State switch
        {
            ConnectivityState.FullyOnline =>
                ("All systems operational", GreenBrush, ""),
            ConnectivityState.InternetDown =>
                ("Internet down — buffering locally", YellowBrush, "Cloud upload suspended. FCC polling continues."),
            ConnectivityState.FccUnreachable =>
                ("FCC unreachable — uploading existing buffer", YellowBrush, "FCC polling suspended. Alert site supervisor."),
            ConnectivityState.FullyOffline =>
                ("Fully offline — serving stale buffer only", RedBrush, "All cloud and FCC workers suspended."),
            _ => ("Unknown state", GrayBrush, "")
        };

        StatusText.Text = text;
        StatusIndicator.Foreground = brush;
        ActionHintText.Text = hint;

        InternetStatus.Text = snapshot.IsInternetUp ? "Connected" : "Disconnected";
        InternetStatus.Foreground = snapshot.IsInternetUp ? GreenBrush : RedBrush;

        FccStatus.Text = snapshot.IsFccUp ? "Connected" : "Disconnected";
        FccStatus.Foreground = snapshot.IsFccUp ? GreenBrush : RedBrush;

        // FCC heartbeat details
        if (_connectivity is not null)
        {
            LastFccHeartbeat.Text = _connectivity.LastFccSuccessAtUtc.HasValue
                ? FormatTimeAgo(_connectivity.LastFccSuccessAtUtc.Value)
                : "Never";
            FccFailures.Text = _connectivity.FccConsecutiveFailures.ToString();
        }
    }

    // ── Periodic refresh ──────────────────────────────────────────────────────

    private async Task RefreshAllAsync()
    {
        if (_services is null) return;

        try
        {
            using var scope = _services.CreateScope();
            var buffer = scope.ServiceProvider.GetService<TransactionBufferManager>();
            if (buffer is null) return;

            var stats = await buffer.GetBufferStatsAsync();

            // Sync state for last-sync timestamp and config version
            var db = scope.ServiceProvider.GetService<AgentDbContext>();
            var syncState = db is not null
                ? await db.SyncStates.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1)
                : null;

            // Device metrics
            var process = Process.GetCurrentProcess();
            var workingSetMb = process.WorkingSet64 / (1024 * 1024);
            var uptime = DateTimeOffset.UtcNow - ProcessStartTime;

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
            catch { /* non-fatal */ }

            Dispatcher.UIThread.Post(() =>
            {
                // Buffer stats
                PendingCount.Text = stats.Pending.ToString("N0");
                UploadedCount.Text = stats.Uploaded.ToString("N0");
                SyncedCount.Text = stats.SyncedToOdoo.ToString("N0");
                TotalCount.Text = stats.Total.ToString("N0");

                // Sync details
                LastCloudSync.Text = syncState?.LastUploadAt.HasValue == true
                    ? FormatTimeAgo(syncState.LastUploadAt!.Value)
                    : "Never";

                if (stats.OldestPendingAtUtc.HasValue)
                {
                    var lag = DateTimeOffset.UtcNow - stats.OldestPendingAtUtc.Value;
                    SyncLag.Text = FormatDuration(lag);
                    OldestPending.Text = stats.OldestPendingAtUtc.Value.LocalDateTime.ToString("g");
                }
                else
                {
                    SyncLag.Text = "None";
                    OldestPending.Text = "None";
                }

                ConfigVersion.Text = syncState?.ConfigVersion ?? "N/A";

                // Device metrics
                UptimeText.Text = FormatDuration(uptime);
                MemoryText.Text = $"{workingSetMb} MB";
                StorageText.Text = storageTotalMb > 0
                    ? $"{storageFreeMb:N0} / {storageTotalMb:N0} MB free"
                    : "N/A";

                // Refresh connectivity details too
                if (_connectivity is not null)
                {
                    LastFccHeartbeat.Text = _connectivity.LastFccSuccessAtUtc.HasValue
                        ? FormatTimeAgo(_connectivity.LastFccSuccessAtUtc.Value)
                        : "Never";
                    FccFailures.Text = _connectivity.FccConsecutiveFailures.ToString();
                }

                // Refresh device identity (F-DSK-033)
                var config = _services?.GetService<IOptionsMonitor<AgentConfiguration>>()?.CurrentValue;
                if (config is not null)
                {
                    DeviceIdText.Text = config.DeviceId ?? "N/A";
                    SiteCodeText.Text = config.SiteId ?? "N/A";
                }
            });
        }
        catch
        {
            // Non-fatal — stats will refresh next cycle
        }
    }

    // ── Static device info (populated once) ───────────────────────────────────

    private void PopulateDeviceInfo()
    {
        var config = _services?.GetService<IOptionsMonitor<AgentConfiguration>>()?.CurrentValue;
        DeviceIdText.Text = config?.DeviceId ?? "N/A";
        SiteCodeText.Text = config?.SiteId ?? "N/A";

        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        AppVersionText.Text = version is not null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";
    }

    // ── Manual action buttons ─────────────────────────────────────────────────

    private async void OnForcePollClicked(object? sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        ForcePollButton.IsEnabled = false;
        SetFeedback("Triggering FCC poll...");

        try
        {
            var ingestion = _services.GetService<IIngestionOrchestrator>();
            if (ingestion is null)
            {
                SetFeedback("FCC poll not available (ingestion service not registered).");
                return;
            }

            var result = await ingestion.ManualPullAsync(null, CancellationToken.None);
            SetFeedback($"FCC poll complete: {result.NewTransactionsBuffered} new, {result.DuplicatesSkipped} duplicates.");
        }
        catch (Exception ex)
        {
            SetFeedback($"FCC poll failed: {ex.Message}");
        }
        finally
        {
            ForcePollButton.IsEnabled = true;
        }
    }

    private async void OnForceSyncClicked(object? sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        ForceSyncButton.IsEnabled = false;
        SetFeedback("Triggering cloud sync...");

        try
        {
            var cloudSync = _services.GetService<ICloudSyncService>();
            if (cloudSync is null)
            {
                SetFeedback("Cloud sync not available (sync service not registered).");
                return;
            }

            var uploaded = await cloudSync.UploadBatchAsync(CancellationToken.None);
            if (uploaded > 0)
                SetFeedback($"Cloud sync complete: {uploaded} transaction(s) processed.");
            else
                SetFeedback("Cloud sync complete: no transactions to upload or upload was skipped.");
        }
        catch (Exception ex)
        {
            SetFeedback($"Cloud sync failed: {ex.Message}");
        }
        finally
        {
            ForceSyncButton.IsEnabled = true;
        }
    }

    private async void OnClearSyncedClicked(object? sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        ClearSyncedButton.IsEnabled = false;
        SetFeedback("Clearing synced cache...");

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

            var now = DateTimeOffset.UtcNow;
            var cleared = await db.Transactions
                .Where(t => t.SyncStatus == Core.Adapter.Common.SyncStatus.SyncedToOdoo
                          || t.SyncStatus == Core.Adapter.Common.SyncStatus.Archived)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.SyncStatus, Core.Adapter.Common.SyncStatus.Archived)
                    .SetProperty(t => t.UpdatedAt, now));

            SetFeedback($"Marked {cleared} synced record(s) as archived.");
        }
        catch (Exception ex)
        {
            SetFeedback($"Clear synced cache failed: {ex.Message}");
        }
        finally
        {
            ClearSyncedButton.IsEnabled = true;
        }
    }

    private void SetFeedback(string message)
    {
        Dispatcher.UIThread.Post(() => ActionFeedback.Text = message);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatTimeAgo(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.UtcNow - timestamp;
        if (elapsed.TotalSeconds < 10) return "Just now";
        if (elapsed.TotalMinutes < 1) return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
    }

    private static DateTimeOffset GetProcessStartTime()
    {
        try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
        catch { return DateTimeOffset.UtcNow; }
    }

    public void Dispose()
    {
        if (_connectivity is not null)
            _connectivity.StateChanged -= OnConnectivityChanged;
        _refreshTimer.Dispose();
    }
}
