using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.App.Views;

public sealed partial class ProvisioningWindow : Window
{
    private readonly IDeviceRegistrationService? _registrationService;
    private readonly IRegistrationManager? _registrationManager;
    private readonly IConfigManager? _configManager;
    private readonly ILogger<ProvisioningWindow>? _logger;

    private int _currentStep = 1;
    private bool _isCodeMethod = true;
    private string _resolvedCloudUrl = string.Empty;
    private string _resolvedSiteCode = string.Empty;
    private string _resolvedFccHost = string.Empty;
    private int _resolvedFccPort;
    private string _resolvedDeviceId = string.Empty;
    private string _apiKey = string.Empty;

    /// <summary>Raised after a successful registration. App.axaml.cs listens to trigger host start + MainWindow.</summary>
    public event EventHandler? RegistrationCompleted;

    public ProvisioningWindow()
    {
        InitializeComponent();

        var services = AgentAppContext.ServiceProvider;
        _registrationService = services?.GetService<IDeviceRegistrationService>();
        _registrationManager = services?.GetService<IRegistrationManager>();
        _configManager = services?.GetService<IConfigManager>();
        _logger = services?.GetService<ILogger<ProvisioningWindow>>();
    }

    // ── Step Navigation ─────────────────────────────────────────────────────

    private void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case 1:
                _isCodeMethod = RadioCodeMethod.IsChecked == true;
                GoToStep(_isCodeMethod ? 2 : 2); // Both go to step 2 (different panels)
                break;
            case 2:
                _ = ExecuteStep2Async();
                break;
            case 3:
                // Step 3 auto-advances, but allow manual retry
                _ = RunConnectionTestsAsync();
                break;
            case 4:
                // Launch agent
                _ = LaunchAgentAsync();
                break;
        }
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case 2:
                GoToStep(1);
                break;
            case 3:
                GoToStep(2);
                break;
        }
    }

    private void GoToStep(int step)
    {
        _currentStep = step;

        // Hide all panels
        Step1Panel.IsVisible = false;
        Step2aPanel.IsVisible = false;
        Step2bPanel.IsVisible = false;
        Step3Panel.IsVisible = false;
        Step4Panel.IsVisible = false;

        // Show current panel
        switch (step)
        {
            case 1:
                Step1Panel.IsVisible = true;
                BackButton.IsVisible = false;
                NextButton.Content = "Next";
                NextButton.IsEnabled = true;
                break;
            case 2:
                if (_isCodeMethod)
                    Step2aPanel.IsVisible = true;
                else
                    Step2bPanel.IsVisible = true;
                BackButton.IsVisible = true;
                NextButton.Content = _isCodeMethod ? "Register" : "Next";
                NextButton.IsEnabled = true;
                break;
            case 3:
                Step3Panel.IsVisible = true;
                BackButton.IsVisible = true;
                NextButton.Content = "Retry Tests";
                NextButton.IsEnabled = false; // Enabled after tests complete
                _ = RunConnectionTestsAsync();
                break;
            case 4:
                Step4Panel.IsVisible = true;
                BackButton.IsVisible = false;
                NextButton.Content = "Launch Agent";
                NextButton.IsEnabled = true;
                break;
        }

        UpdateStepIndicators(step);
    }

    private void UpdateStepIndicators(int activeStep)
    {
        var blue = new SolidColorBrush(Color.Parse("#3B82F6"));
        var green = new SolidColorBrush(Color.Parse("#22C55E"));
        var gray = new SolidColorBrush(Color.Parse("#D1D5DB"));

        // Step bars (border backgrounds inside each indicator)
        var bars = new[] { StepBar2, StepBar3, StepBar4 };
        // Step 1 bar is always blue/green; bars[0]=step2, bars[1]=step3, bars[2]=step4

        for (int i = 0; i < bars.Length; i++)
        {
            int stepNum = i + 2;
            bars[i].Background = stepNum < activeStep ? green
                : stepNum == activeStep ? blue
                : gray;
        }
    }

    // ── Step 2: Registration / Manual Config ────────────────────────────────

    private async Task ExecuteStep2Async()
    {
        if (_isCodeMethod)
            await RegisterWithCodeAsync();
        else
            await ValidateManualConfigAsync();
    }

    private async Task RegisterWithCodeAsync()
    {
        var cloudUrl = CloudUrlBox.Text?.Trim();
        var siteCode = SiteCodeBox.Text?.Trim();
        var token = TokenBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(cloudUrl))
        {
            ShowRegStatus("Please enter the cloud URL.", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(siteCode))
        {
            ShowRegStatus("Please enter the site code.", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(token))
        {
            ShowRegStatus("Please enter the provisioning token.", isError: true);
            return;
        }
        if (_registrationService is null)
        {
            ShowRegStatus("Registration service unavailable.", isError: true);
            return;
        }

        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        NextButton.Content = "Registering...";
        ShowRegStatus("Contacting cloud server...", isError: false);

        try
        {
            var request = DeviceInfoProvider.BuildRequest(
                provisioningToken: token,
                siteCode: siteCode,
                replacePreviousAgent: ReplaceCheck.IsChecked == true);

            var result = await _registrationService.RegisterAsync(cloudUrl, request);

            switch (result)
            {
                case RegistrationResult.Success success:
                    _logger?.LogInformation("Registration successful — deviceId={DeviceId}",
                        success.Response.DeviceId);

                    _resolvedCloudUrl = cloudUrl;
                    _resolvedSiteCode = siteCode;
                    _resolvedDeviceId = success.Response.DeviceId;

                    // Extract FCC details from bootstrap config
                    var siteConfig = success.Response.SiteConfig;
                    if (siteConfig?.Fcc is not null)
                    {
                        _resolvedFccHost = siteConfig.Fcc.HostAddress ?? string.Empty;
                        _resolvedFccPort = siteConfig.Fcc.Port ?? 8080;
                    }

                    GoToStep(3);
                    break;

                case RegistrationResult.Rejected rejected:
                    var hint = GetErrorHint(rejected.Code);
                    ShowRegStatus($"Registration rejected: {rejected.Message}{hint}", isError: true);
                    NextButton.IsEnabled = true;
                    BackButton.IsEnabled = true;
                    NextButton.Content = "Register";
                    break;

                case RegistrationResult.TransportError transport:
                    ShowRegStatus($"Connection error: {transport.Message}", isError: true);
                    NextButton.IsEnabled = true;
                    BackButton.IsEnabled = true;
                    NextButton.Content = "Register";
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error during registration");
            ShowRegStatus($"Unexpected error: {ex.Message}", isError: true);
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            NextButton.Content = "Register";
        }
    }

    private async Task ValidateManualConfigAsync()
    {
        var cloudUrl = ManualCloudUrlBox.Text?.Trim();
        var siteCode = ManualSiteCodeBox.Text?.Trim();
        var fccHost = ManualFccHostBox.Text?.Trim();
        var fccPortText = ManualFccPortBox.Text?.Trim();
        var manualToken = ManualTokenBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(cloudUrl))
        {
            ShowManualStatus("Please enter the cloud URL.", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(siteCode))
        {
            ShowManualStatus("Please enter the site code.", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(fccHost))
        {
            ShowManualStatus("Please enter the FCC host address.", isError: true);
            return;
        }
        if (!int.TryParse(fccPortText, out var fccPort) || fccPort < 1 || fccPort > 65535)
        {
            ShowManualStatus("Please enter a valid FCC port (1-65535).", isError: true);
            return;
        }

        // Validate URL format
        if (!Uri.TryCreate(cloudUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            ShowManualStatus("Cloud URL must be a valid HTTP/HTTPS URL.", isError: true);
            return;
        }

        _resolvedCloudUrl = cloudUrl;
        _resolvedSiteCode = siteCode;
        _resolvedFccHost = fccHost;
        _resolvedFccPort = fccPort;

        // If a provisioning token was provided, perform cloud registration
        // instead of generating a local-only device ID.
        if (!string.IsNullOrWhiteSpace(manualToken))
        {
            await RegisterManualWithTokenAsync(cloudUrl, siteCode, manualToken, fccHost, fccPort);
            return;
        }

        // Offline mode — generate a local device ID (no cloud registration)
        _resolvedDeviceId = $"manual-{Guid.NewGuid():N}"[..24];

        GoToStep(3);
    }

    /// <summary>
    /// Perform cloud registration using a provisioning token entered in the manual config form.
    /// On success, store the cloud-assigned device ID and FCC details, then advance to step 3.
    /// </summary>
    private async Task RegisterManualWithTokenAsync(
        string cloudUrl, string siteCode, string token,
        string fccHost, int fccPort)
    {
        if (_registrationService is null)
        {
            ShowManualStatus("Registration service unavailable.", isError: true);
            return;
        }

        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        NextButton.Content = "Registering...";
        ShowManualStatus("Contacting cloud server...", isError: false);

        try
        {
            var request = DeviceInfoProvider.BuildRequest(
                provisioningToken: token,
                siteCode: siteCode,
                replacePreviousAgent: false);

            var result = await _registrationService.RegisterAsync(cloudUrl, request);

            switch (result)
            {
                case RegistrationResult.Success success:
                    _logger?.LogInformation(
                        "Manual config registration successful — deviceId={DeviceId}",
                        success.Response.DeviceId);

                    _resolvedDeviceId = success.Response.DeviceId;

                    // Use FCC details from bootstrap config if available,
                    // otherwise keep the user-provided values.
                    var siteConfig = success.Response.SiteConfig;
                    if (siteConfig?.Fcc is not null)
                    {
                        _resolvedFccHost = siteConfig.Fcc.HostAddress ?? fccHost;
                        _resolvedFccPort = siteConfig.Fcc.Port ?? fccPort;
                    }

                    // Mark as code method so LaunchAgentAsync skips duplicate state save
                    // (DeviceRegistrationService.RegisterAsync already persisted state).
                    _isCodeMethod = true;

                    GoToStep(3);
                    break;

                case RegistrationResult.Rejected rejected:
                    var hint = GetErrorHint(rejected.Code);
                    ShowManualStatus($"Registration rejected: {rejected.Message}{hint}", isError: true);
                    NextButton.IsEnabled = true;
                    BackButton.IsEnabled = true;
                    NextButton.Content = "Next";
                    break;

                case RegistrationResult.TransportError transport:
                    ShowManualStatus($"Connection error: {transport.Message}", isError: true);
                    NextButton.IsEnabled = true;
                    BackButton.IsEnabled = true;
                    NextButton.Content = "Next";
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error during manual-token registration");
            ShowManualStatus($"Unexpected error: {ex.Message}", isError: true);
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            NextButton.Content = "Next";
        }
    }

    // ── Step 3: Connection Tests ────────────────────────────────────────────

    private async Task RunConnectionTestsAsync()
    {
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        TestOverallBorder.IsVisible = false;

        // Reset indicators
        SetTestState(CloudTestIcon, CloudTestDetail, CloudTestStatus, "...", "Testing...", "In progress");
        SetTestState(FccTestIcon, FccTestDetail, FccTestStatus, "...", "Testing...", "In progress");

        bool cloudOk = false;
        bool fccOk = false;

        // Test cloud connectivity
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await httpClient.GetAsync($"{_resolvedCloudUrl.TrimEnd('/')}/health");
            cloudOk = response.IsSuccessStatusCode;

            if (cloudOk)
                SetTestState(CloudTestIcon, CloudTestDetail, CloudTestStatus,
                    "OK", $"Connected to {_resolvedCloudUrl}", "Connected",
                    "#22C55E");
            else
                SetTestState(CloudTestIcon, CloudTestDetail, CloudTestStatus,
                    "!", $"HTTP {(int)response.StatusCode} from {_resolvedCloudUrl}", "Warning",
                    "#EAB308");
        }
        catch (Exception ex)
        {
            SetTestState(CloudTestIcon, CloudTestDetail, CloudTestStatus,
                "X", $"Failed: {ex.Message}", "Failed",
                "#EF4444");
        }

        // Test FCC connectivity
        if (!string.IsNullOrEmpty(_resolvedFccHost))
        {
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var fccUrl = $"http://{_resolvedFccHost}:{_resolvedFccPort}";
                var response = await httpClient.GetAsync(fccUrl);
                fccOk = true; // Any response means FCC is reachable

                SetTestState(FccTestIcon, FccTestDetail, FccTestStatus,
                    "OK", $"Reachable at {_resolvedFccHost}:{_resolvedFccPort}", "Connected",
                    "#22C55E");
            }
            catch (HttpRequestException)
            {
                SetTestState(FccTestIcon, FccTestDetail, FccTestStatus,
                    "X", $"Cannot reach {_resolvedFccHost}:{_resolvedFccPort}", "Unreachable",
                    "#EF4444");
            }
            catch (TaskCanceledException)
            {
                SetTestState(FccTestIcon, FccTestDetail, FccTestStatus,
                    "X", $"Timeout connecting to {_resolvedFccHost}:{_resolvedFccPort}", "Timeout",
                    "#EF4444");
            }
            catch (Exception ex)
            {
                SetTestState(FccTestIcon, FccTestDetail, FccTestStatus,
                    "X", $"Error: {ex.Message}", "Failed",
                    "#EF4444");
            }
        }
        else
        {
            SetTestState(FccTestIcon, FccTestDetail, FccTestStatus,
                "--", "No FCC host configured (will be set from cloud config)", "Skipped",
                "#6B7280");
            fccOk = true; // Not a blocker — FCC info may come from cloud config
        }

        // Show overall result
        if (cloudOk && fccOk)
        {
            ShowTestOverall("All connection tests passed. You can proceed.", isError: false);
            PopulateSuccessSummary();
            GoToStep(4);
        }
        else if (cloudOk)
        {
            ShowTestOverall(
                "Cloud connected but FCC unreachable. You can still proceed — the agent will retry FCC connection.",
                isError: false);
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            NextButton.Content = "Continue Anyway";
            NextButton.Click -= OnNextClicked;
            NextButton.Click += (_, _) =>
            {
                NextButton.Click -= OnNextClicked; // Clean up temp handler
                NextButton.Click += OnNextClicked;
                PopulateSuccessSummary();
                GoToStep(4);
            };
        }
        else
        {
            ShowTestOverall(
                "Connection tests failed. Please go back and check your settings, or retry.",
                isError: true);
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            NextButton.Content = "Retry Tests";
        }
    }

    private void SetTestState(
        TextBlock icon, TextBlock detail, TextBlock status,
        string iconText, string detailText, string statusText,
        string? color = null)
    {
        icon.Text = iconText;
        detail.Text = detailText;
        status.Text = statusText;

        if (color is not null)
        {
            var brush = new SolidColorBrush(Color.Parse(color));
            icon.Foreground = brush;
            status.Foreground = brush;
        }
    }

    // ── Step 4: Success Summary ─────────────────────────────────────────────

    private void PopulateSuccessSummary()
    {
        SummaryDeviceId.Text = _resolvedDeviceId;
        SummarySiteCode.Text = _resolvedSiteCode;
        SummaryCloudUrl.Text = _resolvedCloudUrl;
        SummaryFccEndpoint.Text = !string.IsNullOrEmpty(_resolvedFccHost)
            ? $"{_resolvedFccHost}:{_resolvedFccPort}"
            : "Will be configured from cloud";

        // Generate or retrieve API key
        var config = AgentAppContext.ServiceProvider?.GetService<IOptions<AgentConfiguration>>()?.Value;
        _apiKey = config?.FccApiKey ?? Guid.NewGuid().ToString("N");
        ApiKeyDisplay.Text = _apiKey;

        var port = config?.LocalApiPort ?? 8585;
        ApiKeyPortHint.Text = $"Local API available at http://<agent-ip>:{port}/api — use header X-Api-Key: <key above>";
    }

    private async void OnCopyApiKeyClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(_apiKey);
                CopyApiKeyButton.Content = "Copied!";
                _ = ResetCopyButtonAsync();
            }
        }
        catch
        {
            CopyApiKeyButton.Content = "Failed";
            _ = ResetCopyButtonAsync();
        }
    }

    private async Task ResetCopyButtonAsync()
    {
        await Task.Delay(2000);
        CopyApiKeyButton.Content = "Copy";
    }

    // ── Launch Agent ────────────────────────────────────────────────────────

    private async Task LaunchAgentAsync()
    {
        NextButton.IsEnabled = false;
        NextButton.Content = "Starting...";

        try
        {
            // For the manual config path, persist registration state now.
            // The code-based path already saved state inside DeviceRegistrationService.RegisterAsync().
            if (!_isCodeMethod && _registrationManager is not null)
            {
                await _registrationManager.SaveStateAsync(new RegistrationState
                {
                    IsRegistered = true,
                    DeviceId = _resolvedDeviceId,
                    SiteCode = _resolvedSiteCode,
                    CloudBaseUrl = _resolvedCloudUrl,
                    RegisteredAt = DateTimeOffset.UtcNow,
                    DeviceModel = Environment.MachineName,
                    OsVersion = Environment.OSVersion.VersionString,
                    AgentVersion = typeof(ProvisioningWindow).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                });
                _logger?.LogInformation(
                    "Manual config registration state persisted (deviceId={DeviceId}, site={SiteCode})",
                    _resolvedDeviceId, _resolvedSiteCode);
            }

            // Persist the generated LAN API key to credential store before starting
            // the host, so ApiKeyMiddleware can load it via PostConfigure.
            var credStore = AgentAppContext.ServiceProvider?.GetService<ICredentialStore>();
            if (credStore is not null && !string.IsNullOrEmpty(_apiKey))
            {
                try
                {
                    await credStore.SetSecretAsync(CredentialKeys.LanApiKey, _apiKey);
                    _logger?.LogInformation("LAN API key persisted to credential store");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to persist LAN API key to credential store");
                }
            }

            // Start the host if not already running
            if (AgentAppContext.WebApp is { } webApp)
            {
                await Task.Run(() => webApp.Start());
                _logger?.LogInformation("Host started after setup wizard");
            }

            // Signal App.axaml.cs to transition to MainWindow
            RegistrationCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start agent host");
            NextButton.IsEnabled = true;
            NextButton.Content = "Retry Launch";
        }
    }

    // ── Status Helpers ──────────────────────────────────────────────────────

    private void ShowRegStatus(string message, bool isError)
    {
        RegStatusBorder.IsVisible = true;
        RegStatusBorder.Background = isError
            ? new SolidColorBrush(Color.FromRgb(254, 226, 226))
            : new SolidColorBrush(Color.FromRgb(220, 252, 231));
        RegStatusText.Text = message;
        RegStatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(153, 27, 27))
            : new SolidColorBrush(Color.FromRgb(22, 101, 52));
    }

    private void ShowManualStatus(string message, bool isError)
    {
        ManualStatusBorder.IsVisible = true;
        ManualStatusBorder.Background = isError
            ? new SolidColorBrush(Color.FromRgb(254, 226, 226))
            : new SolidColorBrush(Color.FromRgb(220, 252, 231));
        ManualStatusText.Text = message;
        ManualStatusText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(153, 27, 27))
            : new SolidColorBrush(Color.FromRgb(22, 101, 52));
    }

    private void ShowTestOverall(string message, bool isError)
    {
        TestOverallBorder.IsVisible = true;
        TestOverallBorder.Background = isError
            ? new SolidColorBrush(Color.FromRgb(254, 226, 226))
            : new SolidColorBrush(Color.FromRgb(220, 252, 231));
        TestOverallText.Text = message;
        TestOverallText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(153, 27, 27))
            : new SolidColorBrush(Color.FromRgb(22, 101, 52));
    }

    private static string GetErrorHint(RegistrationErrorCode code) => code switch
    {
        RegistrationErrorCode.BootstrapTokenExpired => "\n\nThe token has expired. Please generate a new one from the admin portal.",
        RegistrationErrorCode.BootstrapTokenAlreadyUsed => "\n\nThis token has already been used. Please generate a new one.",
        RegistrationErrorCode.BootstrapTokenInvalid => "\n\nThe token is not recognized. Please check you copied it correctly.",
        RegistrationErrorCode.BootstrapTokenRevoked => "\n\nThis token has been revoked by an administrator.",
        RegistrationErrorCode.ActiveAgentExists => "\n\nAnother agent is already registered at this site. Check 'Replace existing agent' to override.",
        RegistrationErrorCode.SiteNotFound => "\n\nThe site code was not found. Please check the site code.",
        RegistrationErrorCode.SiteMismatch => "\n\nThe site code does not match the token. Please verify both values.",
        _ => "",
    };
}
