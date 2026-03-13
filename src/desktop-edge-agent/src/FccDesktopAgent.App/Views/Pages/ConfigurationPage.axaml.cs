using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.App.Views.Pages;

public sealed partial class ConfigurationPage : UserControl, IDisposable
{
    private readonly IServiceProvider? _services;
    private readonly IConfigManager? _configManager;
    private readonly ICredentialStore? _credentialStore;
    private readonly ConfigSaveService? _configSaveService;
    private readonly ILogger<ConfigurationPage>? _logger;
    private AgentConfiguration? _loadedConfig;
    private string? _loadedLanApiKey;
    private bool _disposed;

    public ConfigurationPage()
    {
        InitializeComponent();

        _services = AgentAppContext.ServiceProvider;
        _configManager = _services?.GetService<IConfigManager>();
        _credentialStore = _services?.GetService<ICredentialStore>();
        _configSaveService = _services?.GetService<ConfigSaveService>();
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

        // Local API — load LAN API key from credential store (F-DSK-020)
        CfgApiPort.Value = config.LocalApiPort;
        _ = LoadLanApiKeyAsync();

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

    // T-DSK-016: Config construction and apply orchestration delegated to ConfigSaveService.
    // Code-behind only maps UI controls → ConfigSaveFields and renders the result.
    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        SaveFeedback.Text = "Saving...";

        try
        {
            if (_configSaveService is null)
            {
                SaveFeedback.Text = "Config save service not available.";
                return;
            }

            var fields = new ConfigSaveFields
            {
                FccPollIntervalSeconds = (int)(CfgFccPollInterval.Value ?? 30),
                ConnectivityProbeIntervalSeconds = (int)(CfgConnProbeInterval.Value ?? 30),
                UploadBatchSize = (int)(CfgUploadBatchSize.Value ?? 50),
                CloudSyncIntervalSeconds = (int)(CfgCloudSyncInterval.Value ?? 60),
                ConfigPollIntervalSeconds = (int)(CfgConfigPollInterval.Value ?? 60),
                RetentionDays = (int)(CfgRetentionDays.Value ?? 7),
                CleanupIntervalHours = (int)(CfgCleanupInterval.Value ?? 24),
                LocalApiPort = (int)(CfgApiPort.Value ?? 8585),
                TelemetryIntervalSeconds = (int)(CfgTelemetryInterval.Value ?? 300),
                LogLevel = GetSelectedLogLevel(),
                NewLanApiKey = CfgApiKey.Text?.Trim(),
                PreviousLanApiKey = _loadedLanApiKey,
            };

            var saveResult = await _configSaveService.SaveAsync(fields, CancellationToken.None);
            var result = saveResult.ApplyResult;

            switch (result.Outcome)
            {
                case ConfigApplyOutcome.Applied:
                    SaveFeedback.Text = "Settings saved and applied.";

                    if (!string.IsNullOrEmpty(fields.NewLanApiKey)
                        && fields.NewLanApiKey != _loadedLanApiKey)
                        _loadedLanApiKey = fields.NewLanApiKey;

                    if (result.RestartRequiredSections is { Count: > 0 })
                    {
                        RestartBanner.IsVisible = true;
                        SaveFeedback.Text += $" Restart needed for: {string.Join(", ", result.RestartRequiredSections)}";
                    }

                    if (_loadedConfig is not null && fields.LocalApiPort != _loadedConfig.LocalApiPort)
                        RestartBanner.IsVisible = true;
                    break;

                case ConfigApplyOutcome.StaleVersion:
                    SaveFeedback.Text = "Config version conflict — reload and try again.";
                    LoadCurrentConfig();
                    break;

                case ConfigApplyOutcome.Rejected:
                    SaveFeedback.Text = !string.IsNullOrEmpty(result.ErrorMessage)
                        ? $"Rejected: {result.ErrorMessage}"
                        : "Configuration rejected — check values and try again.";
                    break;

                default:
                    SaveFeedback.Text = $"Apply result: {result.Outcome}";
                    break;
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

    // ── API Key Show/Hide & Regeneration ────────────────────────────────────

    // S-DSK-017: Toggle API key visibility with auto-hide after 10 seconds
    private void OnToggleApiKeyClicked(object? sender, RoutedEventArgs e)
    {
        if (CfgApiKey.PasswordChar == default(char))
        {
            // Hide
            CfgApiKey.PasswordChar = '\u2022';
            ToggleApiKeyButton.Content = "Show";
        }
        else
        {
            // Show — then auto-hide after timeout
            CfgApiKey.PasswordChar = default;
            ToggleApiKeyButton.Content = "Hide";
            _ = AutoHideApiKeyAsync();
        }
    }

    private async Task AutoHideApiKeyAsync()
    {
        await Task.Delay(10_000);
        if (!_disposed)
        {
            Dispatcher.UIThread.Post(() =>
            {
                CfgApiKey.PasswordChar = '\u2022';
                ToggleApiKeyButton.Content = "Show";
            });
        }
    }

    private void OnRegenerateApiKeyClicked(object? sender, RoutedEventArgs e)
    {
        var newKey = Guid.NewGuid().ToString("N");
        CfgApiKey.Text = newKey;
        // Show the new key briefly so the user can verify, then auto-hide
        CfgApiKey.PasswordChar = default;
        ToggleApiKeyButton.Content = "Hide";
        _ = AutoHideApiKeyAsync();
        SaveFeedback.Text = "New LAN API key generated. Click Save & Apply to persist.";
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

    // ── LAN API Key ──────────────────────────────────────────────────────

    private async Task LoadLanApiKeyAsync()
    {
        if (_credentialStore is null) return;
        var key = await _credentialStore.GetSecretAsync(CredentialKeys.LanApiKey);
        _loadedLanApiKey = key;
        Dispatcher.UIThread.Post(() => CfgApiKey.Text = key ?? string.Empty);
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
