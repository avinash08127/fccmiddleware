using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using FccDesktopAgent.App.Services;
using FccDesktopAgent.Core.Config;

namespace FccDesktopAgent.App.Views;

/// <summary>
/// Provisioning wizard window. Business logic (registration, validation, connection testing,
/// state persistence) is delegated to <see cref="SetupOrchestrator"/> (T-DSK-003).
/// Services are injected via constructor (T-DSK-002).
/// </summary>
public sealed partial class ProvisioningWindow : Window
{
    private readonly SetupOrchestrator _orchestrator;

    private static readonly List<string> EnvDisplayItems = BuildEnvDisplayItems();

    private int _currentStep = 1;
    private bool _isCodeMethod = true;

    // M-05: Named reference for the "Continue Anyway" handler so it can be properly removed.
    private EventHandler<RoutedEventArgs>? _continueAnywayHandler;

    // M-08: Cancellation source for in-flight registration calls so users can go back.
    private CancellationTokenSource? _registrationCts;

    // S-DSK-001: Cancellation source for API key auto-hide timer.
    private CancellationTokenSource? _apiKeyVisibilityCts;

    /// <summary>Raised after a successful registration. App.axaml.cs listens to trigger host start + MainWindow.</summary>
    public event EventHandler? RegistrationCompleted;

    public ProvisioningWindow(IServiceProvider? services)
    {
        InitializeComponent();

        _orchestrator = new SetupOrchestrator(services);

        // Populate environment combo boxes
        EnvComboBox.ItemsSource = EnvDisplayItems;
        EnvComboBox.SelectedIndex = 0;
        ManualEnvComboBox.ItemsSource = EnvDisplayItems;
        ManualEnvComboBox.SelectedIndex = 0;
    }

    // ── Step Navigation ─────────────────────────────────────────────────────

    private void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        switch (_currentStep)
        {
            case 1:
                _isCodeMethod = RadioCodeMethod.IsChecked == true;
                GoToStep(2);
                break;
            case 2:
                _ = ExecuteStep2Async();
                break;
            case 3:
                _ = RunConnectionTestsAsync();
                break;
            case 4:
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
                // F-DSK-008: Hide back button after successful cloud registration
                // to prevent re-submission of the consumed one-time token.
                BackButton.IsVisible = !_orchestrator.CloudRegistrationDone;
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

        var bars = new[] { StepBar2, StepBar3, StepBar4 };

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
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        NextButton.Content = "Registering...";
        ShowRegStatus("Contacting cloud server...", isError: false);

        // M-08: 30-second timeout prevents indefinite UI freeze.
        _registrationCts?.Cancel();
        _registrationCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var outcome = await _orchestrator.RegisterWithCodeAsync(
            CloudUrlBox.Text?.Trim(),
            SiteCodeBox.Text?.Trim(),
            TokenBox.Text?.Trim(),
            GetEnvironmentKey(EnvComboBox.SelectedIndex),
            ReplaceCheck.IsChecked == true,
            _registrationCts.Token);

        // S-DSK-004: Clear provisioning token from UI after use.
        TokenBox.Text = string.Empty;

        if (outcome.Kind == RegistrationOutcomeKind.Success)
        {
            GoToStep(3);
            return;
        }

        var msg = outcome.Kind switch
        {
            RegistrationOutcomeKind.Rejected => $"Registration rejected: {outcome.ErrorMessage}{outcome.ErrorHint}",
            RegistrationOutcomeKind.TransportError => $"Connection error: {outcome.ErrorMessage}",
            _ => outcome.ErrorMessage ?? "Registration failed.",
        };
        ShowRegStatus(msg, isError: true);
        NextButton.IsEnabled = true;
        BackButton.IsEnabled = true;
        NextButton.Content = "Register";
    }

    private async Task ValidateManualConfigAsync()
    {
        var validation = _orchestrator.ValidateManualConfig(
            ManualCloudUrlBox.Text?.Trim(),
            ManualSiteCodeBox.Text?.Trim(),
            ManualFccHostBox.Text?.Trim(),
            ManualFccPortBox.Text?.Trim(),
            GetEnvironmentKey(ManualEnvComboBox.SelectedIndex));

        if (!validation.IsValid)
        {
            ShowManualStatus(validation.ErrorMessage!, isError: true);
            return;
        }

        // If a provisioning token was provided, perform cloud registration
        var manualToken = ManualTokenBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(manualToken))
        {
            await RegisterManualWithTokenAsync(manualToken);
            return;
        }

        // Offline mode — local device ID already generated by orchestrator
        GoToStep(3);
    }

    private async Task RegisterManualWithTokenAsync(string token)
    {
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        NextButton.Content = "Registering...";
        ShowManualStatus("Contacting cloud server...", isError: false);

        // M-08: 30-second timeout for manual-token registration path
        _registrationCts?.Cancel();
        _registrationCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var outcome = await _orchestrator.RegisterManualWithTokenAsync(
            _orchestrator.CloudUrl, _orchestrator.SiteCode, token,
            _orchestrator.FccHost, _orchestrator.FccPort,
            _orchestrator.Environment,
            _registrationCts.Token);

        // S-DSK-004: Clear provisioning token from UI after use.
        ManualTokenBox.Text = string.Empty;

        if (outcome.Kind == RegistrationOutcomeKind.Success)
        {
            GoToStep(3);
            return;
        }

        var msg = outcome.Kind switch
        {
            RegistrationOutcomeKind.Rejected => $"Registration rejected: {outcome.ErrorMessage}{outcome.ErrorHint}",
            RegistrationOutcomeKind.TransportError => $"Connection error: {outcome.ErrorMessage}",
            _ => outcome.ErrorMessage ?? "Registration failed.",
        };
        ShowManualStatus(msg, isError: true);
        NextButton.IsEnabled = true;
        BackButton.IsEnabled = true;
        NextButton.Content = "Next";
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

        var results = await _orchestrator.RunConnectionTestsAsync();

        ApplyTestOutcome(CloudTestIcon, CloudTestDetail, CloudTestStatus, results.Cloud);
        ApplyTestOutcome(FccTestIcon, FccTestDetail, FccTestStatus, results.Fcc);

        if (results.AllPassed)
        {
            ShowTestOverall("All connection tests passed. You can proceed.", isError: false);
            PopulateSuccessSummary();
            GoToStep(4);
        }
        else if (results.CloudOnlyPassed)
        {
            ShowTestOverall(
                "Cloud connected but FCC unreachable. You can still proceed — the agent will retry FCC connection.",
                isError: false);
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            NextButton.Content = "Continue Anyway";
            // M-05: Remove the previous "Continue Anyway" handler (if any) and
            // the permanent handler before attaching a new one-shot handler.
            NextButton.Click -= OnNextClicked;
            if (_continueAnywayHandler is not null)
                NextButton.Click -= _continueAnywayHandler;
            _continueAnywayHandler = (_, _) =>
            {
                NextButton.Click -= _continueAnywayHandler;
                NextButton.Click += OnNextClicked;
                _continueAnywayHandler = null;
                PopulateSuccessSummary();
                GoToStep(4);
            };
            NextButton.Click += _continueAnywayHandler;
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

    private static void ApplyTestOutcome(TextBlock icon, TextBlock detail, TextBlock status, TestOutcome outcome)
    {
        var (iconText, statusText, color) = outcome.State switch
        {
            TestState.Connected => ("OK", "Connected", "#22C55E"),
            TestState.Warning => ("!", "Warning", "#EAB308"),
            TestState.Failed => ("X", "Failed", "#EF4444"),
            TestState.Skipped => ("--", "Skipped", "#6B7280"),
            _ => ("?", "Unknown", "#888888"),
        };

        SetTestState(icon, detail, status, iconText, outcome.Detail, statusText, color);
    }

    private static void SetTestState(
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
        _orchestrator.ResolveApiKey();

        SummaryDeviceId.Text = _orchestrator.DeviceId;
        SummarySiteCode.Text = _orchestrator.SiteCode;
        SummaryCloudUrl.Text = _orchestrator.CloudUrl;
        SummaryFccEndpoint.Text = !string.IsNullOrEmpty(_orchestrator.FccHost)
            ? $"{_orchestrator.FccHost}:{_orchestrator.FccPort}"
            : "Will be configured from cloud";

        ApiKeyDisplay.Text = _orchestrator.ApiKey;

        var port = _orchestrator.GetLocalApiPort();
        ApiKeyPortHint.Text = $"Local API available at http://<agent-ip>:{port}/api — use header X-Api-Key: <key above>";
    }

    // S-DSK-001: Toggle API key visibility with auto-hide after 10 seconds.
    private void OnToggleApiKeyClicked(object? sender, RoutedEventArgs e)
    {
        if (ApiKeyDisplay.PasswordChar == '\0')
        {
            ApiKeyDisplay.PasswordChar = '*';
            ToggleApiKeyButton.Content = "Show";
            _apiKeyVisibilityCts?.Cancel();
        }
        else
        {
            ApiKeyDisplay.PasswordChar = '\0';
            ToggleApiKeyButton.Content = "Hide";
            _apiKeyVisibilityCts?.Cancel();
            _apiKeyVisibilityCts = new CancellationTokenSource();
            _ = AutoHideApiKeyAsync(_apiKeyVisibilityCts.Token);
        }
    }

    private async Task AutoHideApiKeyAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            ApiKeyDisplay.PasswordChar = '*';
            ToggleApiKeyButton.Content = "Show";
        }
        catch (OperationCanceledException) { }
    }

    private async void OnCopyApiKeyClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(_orchestrator.ApiKey);
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
            await _orchestrator.PersistManualStateAsync();
            await _orchestrator.PersistApiKeyAsync();

            // F-DSK-007: Only start the host if it is not already running.
            await _orchestrator.StartHostAsync(AgentAppContext.WebApp, AgentAppContext.IsHostStarted);
            AgentAppContext.IsHostStarted = true;

            // Signal App.axaml.cs to transition to MainWindow
            RegistrationCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            NextButton.IsEnabled = true;
            NextButton.Content = "Retry Launch";
        }
    }

    // ── Environment Combo Box ────────────────────────────────────────────────

    private static List<string> BuildEnvDisplayItems()
    {
        var items = CloudEnvironments.DisplayNames.ToList();
        items.Add("Custom (enter URL)");
        return items;
    }

    /// <summary>
    /// Resolves the environment key from the selected combo box index.
    /// Returns <c>null</c> for the "Custom" option.
    /// </summary>
    private static string? GetEnvironmentKey(int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= CloudEnvironments.Keys.Count)
            return null;
        return CloudEnvironments.Keys[selectedIndex];
    }

    private void OnEnvSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyEnvSelection(EnvComboBox.SelectedIndex, CloudUrlBox);
    }

    private void OnManualEnvSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyEnvSelection(ManualEnvComboBox.SelectedIndex, ManualCloudUrlBox);
    }

    private static void ApplyEnvSelection(int selectedIndex, TextBox urlBox)
    {
        var envKey = GetEnvironmentKey(selectedIndex);
        var resolved = envKey is not null ? CloudEnvironments.Resolve(envKey) : null;

        if (resolved is not null)
        {
            urlBox.Text = resolved;
            urlBox.IsReadOnly = true;
        }
        else
        {
            urlBox.IsReadOnly = false;
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
}
