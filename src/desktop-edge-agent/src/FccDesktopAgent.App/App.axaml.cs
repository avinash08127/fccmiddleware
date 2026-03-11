using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FccDesktopAgent.App.Views;
using FccDesktopAgent.Core.Connectivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.App;

public sealed partial class App : Application
{
    private TrayIconManager? _trayIconManager;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Prevent auto-exit when the last window is closed — tray keeps the app alive
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Wire tray icon
            var services = AgentAppContext.ServiceProvider;
            var logger = services?.GetService<ILogger<TrayIconManager>>()
                ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<TrayIconManager>();
            var connectivity = services?.GetService<IConnectivityMonitor>();

            _trayIconManager = new TrayIconManager(logger, connectivity);
            _trayIconManager.AttachToApplication(this);

            _trayIconManager.ShowDashboardRequested += (_, _) =>
            {
                mainWindow.Show();
                mainWindow.Activate();
            };

            _trayIconManager.RestartAgentRequested += (_, _) =>
            {
                // DEA-2.x: trigger host restart via IHostApplicationLifetime
                logger.LogWarning("Agent restart requested via tray — not yet implemented (DEA-2.x)");
            };

            _trayIconManager.ExitRequested += (_, _) =>
            {
                mainWindow.ForceClose();
                desktop.Shutdown();
            };

            // Handle clean-up when desktop lifetime exits
            desktop.Exit += (_, _) => _trayIconManager?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
