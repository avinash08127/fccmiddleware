using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FccDesktopAgent.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.App.Views.Pages;

public sealed partial class ConfigurationPage : UserControl, IDisposable
{
    private readonly IServiceProvider? _services;
    private readonly IConfigManager? _configManager;
    private readonly ILogger<ConfigurationPage>? _logger;
    private AgentConfiguration? _loadedConfig;
    private bool _disposed;

    public ConfigurationPage()
    {
        InitializeComponent();

        _services = AgentAppContext.ServiceProvider;
        _configManager = _services?.GetService<IConfigManager>();
        _logger = _services?.GetService<ILogger<ConfigurationPage>>();

        // Subscribe to config changes for live updates
        if (_configManager is not null)
            _configManager.ConfigChanged += OnConfigChanged;

        LoadCurrentConfig();
    }

    // ── Load Config ─────────────────────────────────────────────────────────

    private void LoadCurrentConfig()
    {
        var config = _services?.GetService<IOptionsMonitor<AgentConfiguration>>()?.CurrentValue;
        if (config is null) return;

        _loadedConfig = config;

        // Device Identity (read-only)
        CfgDeviceId.Text = config.DeviceId;
        CfgSiteId.Text = config.SiteId;
        CfgCloudUrl.Text = config.CloudBaseUrl;
        CfgConfigVersion.Text = _configManager?.CurrentConfigVersion ?? "N/A";

        // FCC Connection (read-only)
        CfgFccBaseUrl.Text = config.FccBaseUrl;
        CfgFccVendor.Text = config.FccVendor.ToString();
        CfgIngestionMode.Text = config.IngestionMode.ToString();

        // Polling Intervals (editable)
        CfgFccPollInterval.Value = config.FccPollIntervalSeconds;
        CfgCloudSyncInterval.Value = config.CloudSyncIntervalSeconds;
        CfgConfigPollInterval.Value = config.ConfigPollIntervalSeconds;
        CfgTelemetryInterval.Value = config.TelemetryIntervalSeconds;

        // Buffer Settings (editable)
        CfgRetentionDays.Value = config.RetentionDays;
        CfgUploadBatchSize.Value = config.UploadBatchSize;
        CfgCleanupInterval.Value = config.CleanupIntervalHours;

        // Local API
        CfgApiPort.Value = config.LocalApiPort;
        CfgApiKey.Text = config.FccApiKey;

        // Auto-Update
        CfgAutoUpdateEnabled.IsChecked = config.AutoUpdateEnabled;

        // Logging
        SelectLogLevel(GetCurrentLogLevel());

        // Connectivity
        CfgConnProbeInterval.Value = config.ConnectivityProbeIntervalSeconds;
        CfgPreAuthTimeout.Value = config.PreAuthTimeoutSeconds;

        // Show restart banner if needed
        if (_configManager?.RestartRequired == true)
            RestartBanner.IsVisible = true;
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LoadCurrentConfig();
            if (e.RestartRequiredSections.Count > 0)
                RestartBanner.IsVisible = true;
        });
    }

    // ── Save Config ─────────────────────────────────────────────────────────

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        SaveFeedback.Text = "Saving...";

        try
        {
            // Build updated SiteConfig from editable fields
            var currentSite = _configManager?.CurrentSiteConfig;

            // Create a new SiteConfig with local overrides applied
            var updated = new SiteConfig
            {
                SchemaVersion = currentSite?.SchemaVersion ?? "1.0",
                ConfigVersion = (currentSite?.ConfigVersion ?? 0) + 1,
                ConfigId = currentSite?.ConfigId ?? Guid.NewGuid().ToString(),
                IssuedAtUtc = DateTimeOffset.UtcNow,
                EffectiveAtUtc = DateTimeOffset.UtcNow,
                Identity = currentSite?.Identity,
                Site = currentSite?.Site,
                Fcc = new SiteConfigFcc
                {
                    Enabled = currentSite?.Fcc?.Enabled ?? true,
                    FccId = currentSite?.Fcc?.FccId,
                    Vendor = currentSite?.Fcc?.Vendor,
                    ConnectionProtocol = currentSite?.Fcc?.ConnectionProtocol,
                    HostAddress = currentSite?.Fcc?.HostAddress,
                    Port = currentSite?.Fcc?.Port,
                    CredentialRef = currentSite?.Fcc?.CredentialRef,
                    TransactionMode = currentSite?.Fcc?.TransactionMode,
                    IngestionMode = currentSite?.Fcc?.IngestionMode,
                    PullIntervalSeconds = (int)(CfgFccPollInterval.Value ?? 30),
                    HeartbeatIntervalSeconds = (int)(CfgConnProbeInterval.Value ?? 30),
                    HeartbeatTimeoutSeconds = currentSite?.Fcc?.HeartbeatTimeoutSeconds ?? 60,
                },
                Sync = new SiteConfigSync
                {
                    CloudBaseUrl = currentSite?.Sync?.CloudBaseUrl,
                    UploadBatchSize = (int)(CfgUploadBatchSize.Value ?? 50),
                    UploadIntervalSeconds = (int)(CfgCloudSyncInterval.Value ?? 60),
                    ConfigPollIntervalSeconds = (int)(CfgConfigPollInterval.Value ?? 60),
                    CursorStrategy = currentSite?.Sync?.CursorStrategy,
                },
                Buffer = new SiteConfigBuffer
                {
                    RetentionDays = (int)(CfgRetentionDays.Value ?? 7),
                    MaxRecords = currentSite?.Buffer?.MaxRecords ?? 30_000,
                    CleanupIntervalHours = (int)(CfgCleanupInterval.Value ?? 24),
                    PersistRawPayloads = currentSite?.Buffer?.PersistRawPayloads ?? false,
                },
                LocalApi = new SiteConfigLocalApi
                {
                    LocalhostPort = (int)(CfgApiPort.Value ?? 8585),
                    EnableLanApi = currentSite?.LocalApi?.EnableLanApi ?? false,
                    LanBindAddress = currentSite?.LocalApi?.LanBindAddress,
                    LanApiKeyRef = currentSite?.LocalApi?.LanApiKeyRef,
                    RateLimitPerMinute = currentSite?.LocalApi?.RateLimitPerMinute ?? 60,
                },
                Telemetry = new SiteConfigTelemetry
                {
                    TelemetryIntervalSeconds = (int)(CfgTelemetryInterval.Value ?? 300),
                    LogLevel = GetSelectedLogLevel(),
                    IncludeDiagnosticsLogs = currentSite?.Telemetry?.IncludeDiagnosticsLogs ?? false,
                    MetricsWindowSeconds = currentSite?.Telemetry?.MetricsWindowSeconds ?? 300,
                },
                Fiscalization = currentSite?.Fiscalization,
                Mappings = currentSite?.Mappings,
                Rollout = currentSite?.Rollout,
            };

            #pragma warning disable IL2026 // Trimming — SiteConfig is always preserved
            var rawJson = System.Text.Json.JsonSerializer.Serialize(updated,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            #pragma warning restore IL2026

            if (_configManager is not null)
            {
                var result = await _configManager.ApplyConfigAsync(
                    updated, rawJson, updated.ConfigVersion.ToString(), CancellationToken.None);

                switch (result.Outcome)
                {
                    case ConfigApplyOutcome.Applied:
                        SaveFeedback.Text = "Settings saved and applied.";

                        // Check for restart-required changes
                        if (result.RestartRequiredSections is { Count: > 0 })
                        {
                            RestartBanner.IsVisible = true;
                            SaveFeedback.Text += $" Restart needed for: {string.Join(", ", result.RestartRequiredSections)}";
                        }

                        // Check port change (restart-required)
                        if (_loadedConfig is not null && (int)(CfgApiPort.Value ?? 0) != _loadedConfig.LocalApiPort)
                        {
                            RestartBanner.IsVisible = true;
                        }
                        break;

                    case ConfigApplyOutcome.StaleVersion:
                        SaveFeedback.Text = "Config version conflict — reload and try again.";
                        LoadCurrentConfig();
                        break;

                    default:
                        SaveFeedback.Text = $"Apply result: {result.Outcome}";
                        break;
                }
            }
            else
            {
                SaveFeedback.Text = "Config manager not available.";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save configuration");
            SaveFeedback.Text = $"Error: {ex.Message}";
        }
        finally
        {
            SaveButton.IsEnabled = true;
            _ = ClearFeedbackAsync();
        }
    }

    // ── API Key Regeneration ────────────────────────────────────────────────

    private void OnRegenerateApiKeyClicked(object? sender, RoutedEventArgs e)
    {
        var newKey = Guid.NewGuid().ToString("N");
        CfgApiKey.Text = newKey;
        SaveFeedback.Text = "New API key generated. Click Save & Apply to persist.";
        _ = ClearFeedbackAsync();
    }

    // ── Auto-Update Check ───────────────────────────────────────────────────

    private async void OnCheckUpdateClicked(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates...";

        try
        {
            var updateService = _services?.GetService<IUpdateService>();
            if (updateService is null)
            {
                UpdateStatusText.Text = "Update service not available.";
                return;
            }

            var result = await updateService.CheckForUpdatesAsync();
            if (result.UpdateAvailable && result.Downloaded)
                UpdateStatusText.Text = $"Update {result.AvailableVersion} downloaded. Restart to apply.";
            else if (result.UpdateAvailable)
                UpdateStatusText.Text = $"Update {result.AvailableVersion} available.";
            else if (result.ErrorMessage is not null)
                UpdateStatusText.Text = $"Check failed: {result.ErrorMessage}";
            else
                UpdateStatusText.Text = "You're on the latest version.";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    // ── Log Level Helpers ───────────────────────────────────────────────────

    private string GetCurrentLogLevel()
    {
        var siteConfig = _configManager?.CurrentSiteConfig;
        return siteConfig?.Telemetry?.LogLevel ?? "Information";
    }

    private void SelectLogLevel(string level)
    {
        for (int i = 0; i < CfgLogLevel.Items.Count; i++)
        {
            if (CfgLogLevel.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag?.ToString(), level, StringComparison.OrdinalIgnoreCase))
            {
                CfgLogLevel.SelectedIndex = i;
                return;
            }
        }
        CfgLogLevel.SelectedIndex = 2; // Default to Information
    }

    private string GetSelectedLogLevel()
    {
        if (CfgLogLevel.SelectedItem is ComboBoxItem item)
            return item.Tag?.ToString() ?? "Information";
        return "Information";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task ClearFeedbackAsync()
    {
        await Task.Delay(5000);
        if (!_disposed)
            Dispatcher.UIThread.Post(() => SaveFeedback.Text = string.Empty);
    }

    public void Dispose()
    {
        _disposed = true;
        if (_configManager is not null)
            _configManager.ConfigChanged -= OnConfigChanged;
    }
}
