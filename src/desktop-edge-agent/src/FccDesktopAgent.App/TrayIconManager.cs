using Avalonia;
using Avalonia.Controls;
using FccDesktopAgent.Core.Connectivity;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.App;

/// <summary>
/// Manages the system tray icon and its context menu for the duration of the application.
/// Subscribes to <see cref="IConnectivityMonitor"/> (when available) and updates the
/// tooltip text to reflect current connectivity state.
/// Dynamic icon colour (green/yellow/red) is a DEA-2.x enhancement — for now the
/// platform's default application icon is used.
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly ILogger<TrayIconManager> _logger;
    private readonly IConnectivityMonitor? _connectivity;

    /// <summary>Raised when the user clicks "Show Dashboard" in the tray menu.</summary>
    public event EventHandler? ShowDashboardRequested;

    /// <summary>Raised when the user clicks "Restart Agent" in the tray menu.</summary>
    public event EventHandler? RestartAgentRequested;

    /// <summary>Raised when the user clicks "Exit" in the tray menu.</summary>
    public event EventHandler? ExitRequested;

    public TrayIconManager(ILogger<TrayIconManager> logger, IConnectivityMonitor? connectivity = null)
    {
        _logger = logger;
        _connectivity = connectivity;

        _trayIcon = new TrayIcon
        {
            ToolTipText = "FCC Desktop Agent — Starting...",
            IsVisible = true
        };

        _trayIcon.Menu = BuildMenu();

        if (_connectivity is not null)
        {
            _connectivity.StateChanged += OnConnectivityStateChanged;
            UpdateTooltip(_connectivity.Current.State);
        }
    }

    /// <summary>
    /// Registers this tray icon with the Avalonia <see cref="Application"/> instance.
    /// Must be called during or after <c>OnFrameworkInitializationCompleted</c>.
    /// </summary>
    public void AttachToApplication(Application app)
    {
        TrayIcon.SetIcons(app, [_trayIcon]);
        _logger.LogDebug("Tray icon attached to application");
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("Show Dashboard");
        showItem.Click += (_, _) =>
        {
            _logger.LogDebug("Tray: Show Dashboard clicked");
            ShowDashboardRequested?.Invoke(this, EventArgs.Empty);
        };

        var restartItem = new NativeMenuItem("Restart Agent");
        restartItem.Click += (_, _) =>
        {
            _logger.LogInformation("Tray: Restart Agent clicked");
            RestartAgentRequested?.Invoke(this, EventArgs.Empty);
        };

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) =>
        {
            _logger.LogInformation("Tray: Exit clicked");
            ExitRequested?.Invoke(this, EventArgs.Empty);
        };

        menu.Add(showItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(restartItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exitItem);

        return menu;
    }

    private void OnConnectivityStateChanged(object? sender, ConnectivitySnapshot snapshot)
    {
        // Avalonia tray tooltip updates must happen on the UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateTooltip(snapshot.State));
    }

    private void UpdateTooltip(ConnectivityState state)
    {
        _trayIcon.ToolTipText = state switch
        {
            ConnectivityState.FullyOnline     => "FCC Desktop Agent — Online",
            ConnectivityState.InternetDown    => "FCC Desktop Agent — Internet Down",
            ConnectivityState.FccUnreachable  => "FCC Desktop Agent — FCC Unreachable",
            ConnectivityState.FullyOffline    => "FCC Desktop Agent — Offline",
            _                                 => "FCC Desktop Agent"
        };
    }

    public void Dispose()
    {
        if (_connectivity is not null)
            _connectivity.StateChanged -= OnConnectivityStateChanged;

        _trayIcon.Dispose();
    }
}
