using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.App.Views;

public sealed partial class SettingsPanel : UserControl
{
    private readonly IServiceProvider? _services;
    private readonly LocalOverrideManager? _overrideManager;
    private readonly ILogger<SettingsPanel>? _logger;

    public SettingsPanel()
    {
        InitializeComponent();

        _services = AgentAppContext.ServiceProvider;
        _overrideManager = _services?.GetService<LocalOverrideManager>();
        _logger = _services?.GetService<ILogger<SettingsPanel>>();

        LoadValues();
    }

    // ── Load ────────────────────────────────────────────────────────────────

    private void LoadValues()
    {
        if (_overrideManager is null) return;

        // Cloud defaults from current config
        var config = _services?.GetService<IOptionsMonitor<AgentConfiguration>>()?.CurrentValue;
        if (config is not null && Uri.TryCreate(config.FccBaseUrl, UriKind.Absolute, out var uri))
        {
            CloudHost.Text = uri.Host;
            CloudPort.Text = uri.Port.ToString();
        }
        else
        {
            CloudHost.Text = config?.FccBaseUrl ?? "(not configured)";
            CloudPort.Text = "";
        }

        // Cloud API Routes
        var cloudBaseUrl = config?.CloudBaseUrl?.TrimEnd('/') ?? string.Empty;
        PopulateRoutes(cloudBaseUrl);

        // Current override values (empty = using cloud defaults)
        OverrideFccHost.Text = _overrideManager.FccHost ?? string.Empty;
        OverrideFccPort.Text = _overrideManager.FccPort?.ToString() ?? string.Empty;
        OverrideJplPort.Text = _overrideManager.JplPort?.ToString() ?? string.Empty;
        OverrideWsPort.Text = _overrideManager.WsPort?.ToString() ?? string.Empty;

        OverrideBanner.IsVisible = _overrideManager.HasOverrides();
    }

    // ── Save & Reconnect ────────────────────────────────────────────────────

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_overrideManager is null)
        {
            FeedbackText.Text = "Override manager not available.";
            return;
        }

        SaveButton.IsEnabled = false;
        FeedbackText.Text = "Saving...";

        try
        {
            int? fccPort = ParsePortOrNull(OverrideFccPort.Text);
            int? jplPort = ParsePortOrNull(OverrideJplPort.Text);
            int? wsPort = ParsePortOrNull(OverrideWsPort.Text);
            string? fccHost = string.IsNullOrWhiteSpace(OverrideFccHost.Text) ? null : OverrideFccHost.Text.Trim();

            _overrideManager.SaveAll(fccHost, fccPort, jplPort, wsPort);
            OverrideBanner.IsVisible = _overrideManager.HasOverrides();

            // Trigger adapter reconnect with new config
            var ingestion = _services?.GetService<IIngestionOrchestrator>();
            if (ingestion is not null)
            {
                try
                {
                    await ingestion.EnsurePushListenersInitializedAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Push listener re-initialization failed after override save");
                }
            }

            FeedbackText.Text = "Overrides saved. Adapter will use new values on next connection.";
            _logger?.LogInformation("FCC overrides saved — host={Host}, port={Port}, jplPort={JplPort}, wsPort={WsPort}",
                fccHost ?? "(cloud)", fccPort?.ToString() ?? "(cloud)",
                jplPort?.ToString() ?? "(cloud)", wsPort?.ToString() ?? "(cloud)");
        }
        catch (Exception ex)
        {
            FeedbackText.Text = $"Error: {ex.Message}";
            _logger?.LogError(ex, "Failed to save FCC overrides");
        }
        finally
        {
            SaveButton.IsEnabled = true;
            _ = ClearFeedbackAsync();
        }
    }

    // ── Reset ───────────────────────────────────────────────────────────────

    private void OnResetClicked(object? sender, RoutedEventArgs e)
    {
        if (_overrideManager is null) return;

        _overrideManager.ClearAllOverrides();
        LoadValues();
        FeedbackText.Text = "Overrides cleared. Using cloud defaults.";
        _logger?.LogInformation("FCC overrides cleared — reverted to cloud defaults");
        _ = ClearFeedbackAsync();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void PopulateRoutes(string cloudBaseUrl)
    {
        string FormatRoute(string path) =>
            string.IsNullOrEmpty(cloudBaseUrl) ? "(not configured)" : $"{cloudBaseUrl}{path}";

        RouteRegistration.Text = FormatRoute("/api/v1/agent/register");
        RouteConfigPoll.Text = FormatRoute("/api/v1/agent/config");
        RouteTokenRefresh.Text = FormatRoute("/api/v1/agent/token/refresh");
        RouteTransactionUpload.Text = FormatRoute("/api/v1/transactions/upload");
        RouteSyncedStatus.Text = FormatRoute("/api/v1/transactions/synced-status");
        RoutePreAuth.Text = FormatRoute("/api/v1/preauth");
        RouteTelemetry.Text = FormatRoute("/api/v1/agent/telemetry");
        RouteDiagnosticLogs.Text = FormatRoute("/api/v1/agent/diagnostic-logs");
        RouteVersionCheck.Text = FormatRoute("/api/v1/agent/version-check");
    }

    private static int? ParsePortOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!int.TryParse(text.Trim(), out var port))
            throw new ArgumentException($"Port must be a number: '{text}'");
        if (port is < 1 or > 65535)
            throw new ArgumentException($"Port out of range: {port}. Must be 1-65535.");
        return port;
    }

    private async Task ClearFeedbackAsync()
    {
        await Task.Delay(5000);
        Dispatcher.UIThread.Post(() => FeedbackText.Text = string.Empty);
    }
}
