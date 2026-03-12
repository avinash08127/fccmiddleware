using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FccDesktopAgent.App.Services;
using FccDesktopAgent.App.Views.Pages;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Connectivity;
using Microsoft.Extensions.DependencyInjection;

namespace FccDesktopAgent.App.Views;

public sealed partial class MainWindow : Window, IDisposable
{
    private bool _forceClose;
    private bool _firstCloseNotified;
    private string _currentNav = "Dashboard";
    private readonly IConnectivityMonitor? _connectivity;
    private readonly IServiceProvider? _services;
    private readonly Timer _statusTimer;
    private readonly CancellationTokenSource _disposeCts = new();

    // Lazy-created pages — kept alive for the window's lifetime
    private DashboardPage? _dashboardPage;
    private TransactionsPage? _transactionsPage;
    private PreAuthPage? _preAuthPage;
    private ConfigurationPage? _configurationPage;
    private LogsPage? _logsPage;
    private SettingsPanel? _settingsPanel;

    public MainWindow()
    {
        InitializeComponent();

        _services = AgentAppContext.ServiceProvider;
        _connectivity = _services?.GetService<IConnectivityMonitor>();

        // Subscribe to connectivity for status bar
        if (_connectivity is not null)
        {
            _connectivity.StateChanged += OnConnectivityChanged;
            UpdateStatusBarConnectivity(_connectivity.Current);
        }

        // Poll buffer stats for status bar every 5s
        _statusTimer = new Timer(_ => _ = RefreshStatusBarAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

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

    private void OnNavClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string target)
        {
            NavigateTo(target);
            UpdateNavHighlight(target);
        }
    }

    private void NavigateTo(string target)
    {
        _currentNav = target;

        PageContent.Content = target switch
        {
            "Dashboard" => _dashboardPage ??= new DashboardPage(),
            "Transactions" => _transactionsPage ??= new TransactionsPage(),
            "PreAuth" => _preAuthPage ??= new PreAuthPage(),
            "Configuration" => _configurationPage ??= new ConfigurationPage(),
            "Logs" => _logsPage ??= new LogsPage(),
            "Settings" => _settingsPanel ??= new SettingsPanel(),
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

    // ── Status Bar ──────────────────────────────────────────────────────────

    private void OnConnectivityChanged(object? sender, ConnectivitySnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => UpdateStatusBarConnectivity(snapshot));
    }

    private void UpdateStatusBarConnectivity(ConnectivitySnapshot snapshot)
    {
        var (text, color) = snapshot.State switch
        {
            ConnectivityState.FullyOnline => ("Online", "#22C55E"),
            ConnectivityState.InternetDown => ("Internet Down", "#EAB308"),
            ConnectivityState.FccUnreachable => ("FCC Unreachable", "#EAB308"),
            ConnectivityState.FullyOffline => ("Offline", "#EF4444"),
            _ => ("Unknown", "#888888")
        };

        StatusConnectivity.Text = text;
        StatusDot.Foreground = new SolidColorBrush(Color.Parse(color));
    }

    private DateTimeOffset? _lastSyncTime;

    private async Task RefreshStatusBarAsync()
    {
        // L-03: Guard against timer callbacks arriving after Dispose
        if (_services is null || _disposeCts.IsCancellationRequested) return;

        try
        {
            using var scope = _services.CreateScope();
            var buffer = scope.ServiceProvider.GetService<TransactionBufferManager>();
            if (buffer is null) return;

            var stats = await buffer.GetBufferStatsAsync();

            Dispatcher.UIThread.Post(() =>
            {
                StatusBuffer.Text = $"{stats.Pending:N0} pending";

                if (_connectivity?.Current.IsInternetUp == true)
                {
                    _lastSyncTime = DateTimeOffset.UtcNow;
                    StatusLastSync.Text = "Last sync: Just now";
                }
                else if (_lastSyncTime.HasValue)
                {
                    var elapsed = DateTimeOffset.UtcNow - _lastSyncTime.Value;
                    var ago = elapsed.TotalMinutes < 1 ? "Just now"
                        : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
                        : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago"
                        : $"{(int)elapsed.TotalDays}d ago";
                    StatusLastSync.Text = $"Last sync: {ago}";
                }
            });
        }
        catch
        {
            // Non-fatal
        }
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

        // Only restore position if it's on-screen (basic check)
        if (state.X >= 0 && state.Y >= 0)
        {
            Position = new PixelPoint((int)state.X, (int)state.Y);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        if (state.IsMaximized)
        {
            WindowState = Avalonia.Controls.WindowState.Maximized;
        }
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
        // L-03: Signal cancellation before disposing the timer so in-flight
        // callbacks see the flag and bail out before touching disposed resources.
        _disposeCts.Cancel();
        if (_connectivity is not null)
            _connectivity.StateChanged -= OnConnectivityChanged;
        _statusTimer.Dispose();
        _disposeCts.Dispose();
        (_dashboardPage as IDisposable)?.Dispose();
        (_transactionsPage as IDisposable)?.Dispose();
        (_configurationPage as IDisposable)?.Dispose();
        (_logsPage as IDisposable)?.Dispose();
    }
}
