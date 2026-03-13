using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using FccDesktopAgent.App.Services;
using FccDesktopAgent.App.ViewModels;
using FccDesktopAgent.App.Views.Pages;

namespace FccDesktopAgent.App.Views;

/// <summary>
/// Main dashboard window. Status bar state and navigation commands are owned
/// by <see cref="MainWindowViewModel"/> (T-DSK-001). Services are injected via
/// constructor (T-DSK-002) instead of the static AgentAppContext service locator.
/// </summary>
public sealed partial class MainWindow : Window, IDisposable
{
    private bool _forceClose;
    private bool _firstCloseNotified;
    private readonly IServiceProvider? _services;
    private readonly MainWindowViewModel _viewModel;

    // Lazy-created pages — kept alive for the window's lifetime
    private DashboardPage? _dashboardPage;
    private TransactionsPage? _transactionsPage;
    private PreAuthPage? _preAuthPage;
    private ConfigurationPage? _configurationPage;
    private LogsPage? _logsPage;
    private SettingsPanel? _settingsPanel;

    public MainWindow(IServiceProvider? services)
    {
        InitializeComponent();

        _services = services;
        _viewModel = new MainWindowViewModel(services);
        DataContext = _viewModel;

        // React to navigation changes from ViewModel
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.CurrentNavTarget))
            {
                NavigateTo(_viewModel.CurrentNavTarget);
                UpdateNavHighlight(_viewModel.CurrentNavTarget);
            }
        };

        // Default to Dashboard
        NavigateTo("Dashboard");
        UpdateNavHighlight("Dashboard");

        // Restore window position
        RestoreWindowState();
    }

    /// <summary>
    /// Called by the tray "Exit" handler to bypass the minimize-to-tray behaviour
    /// and allow the window (and application) to close normally.
    /// </summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose)
        {
            // Minimize to tray: cancel the close and hide the window instead.
            e.Cancel = true;
            Hide();

            if (!_firstCloseNotified)
            {
                _firstCloseNotified = true;
                // Could show a notification here — for now just log
            }

            return;
        }

        // Save window state before closing
        SaveWindowState();

        // Dispose page resources
        (_dashboardPage as IDisposable)?.Dispose();
        (_transactionsPage as IDisposable)?.Dispose();
        (_configurationPage as IDisposable)?.Dispose();
        (_logsPage as IDisposable)?.Dispose();

        base.OnClosing(e);
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    private void NavigateTo(string target)
    {
        PageContent.Content = target switch
        {
            "Dashboard" => _dashboardPage ??= new DashboardPage(),
            "Transactions" => _transactionsPage ??= new TransactionsPage(),
            "PreAuth" => _preAuthPage ??= new PreAuthPage(),
            "Configuration" => _configurationPage ??= new ConfigurationPage(),
            "Logs" => _logsPage ??= new LogsPage(),
            "Settings" => _settingsPanel ??= new SettingsPanel(_services),
            _ => _dashboardPage ??= new DashboardPage()
        };
    }

    private void UpdateNavHighlight(string activeTarget)
    {
        var buttons = new (Button btn, string tag)[]
        {
            (NavDashboard, "Dashboard"),
            (NavTransactions, "Transactions"),
            (NavPreAuth, "PreAuth"),
            (NavConfiguration, "Configuration"),
            (NavLogs, "Logs"),
            (NavSettings, "Settings")
        };

        foreach (var (btn, tag) in buttons)
        {
            btn.FontWeight = tag == activeTarget ? FontWeight.SemiBold : FontWeight.Normal;
            btn.Opacity = tag == activeTarget ? 1.0 : 0.7;
        }
    }

    // ── Theme Toggle ────────────────────────────────────────────────────────

    private void OnThemeToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is null) return;

        var current = Application.Current.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant =
            current == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    // ── Window State Persistence ────────────────────────────────────────────

    private void RestoreWindowState()
    {
        var state = WindowStateService.Load();
        if (state is null) return;

        // Validate saved dimensions are reasonable
        if (state.Width >= MinWidth && state.Height >= MinHeight)
        {
            Width = state.Width;
            Height = state.Height;
        }

        // F-DSK-005: Validate saved position against all connected screens.
        if (state.X >= 0 && state.Y >= 0 && IsPositionOnAnyScreen((int)state.X, (int)state.Y))
        {
            Position = new PixelPoint((int)state.X, (int)state.Y);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        if (state.IsMaximized)
        {
            WindowState = Avalonia.Controls.WindowState.Maximized;
        }
    }

    private bool IsPositionOnAnyScreen(int x, int y)
    {
        var screens = Screens;
        if (screens is null) return true; // can't verify — allow restore
        foreach (var screen in screens.All)
        {
            var bounds = screen.WorkingArea;
            if (x >= bounds.X && x < bounds.X + bounds.Width &&
                y >= bounds.Y && y < bounds.Y + bounds.Height)
                return true;
        }
        return false;
    }

    private void SaveWindowState()
    {
        var state = new Services.WindowState
        {
            X = Position.X,
            Y = Position.Y,
            Width = Width,
            Height = Height,
            IsMaximized = WindowState == Avalonia.Controls.WindowState.Maximized
        };

        WindowStateService.Save(state);
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        (_dashboardPage as IDisposable)?.Dispose();
        (_transactionsPage as IDisposable)?.Dispose();
        (_configurationPage as IDisposable)?.Dispose();
        (_logsPage as IDisposable)?.Dispose();
    }
}
