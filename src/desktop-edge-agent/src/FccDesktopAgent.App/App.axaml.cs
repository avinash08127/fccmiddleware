using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FccDesktopAgent.App.Views;
using FccDesktopAgent.Core.Config;
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

            switch (AgentAppContext.Mode)
            {
                case StartupMode.Decommissioned:
                    InitializeDecommissionedMode(desktop);
                    break;

                case StartupMode.Provisioning:
                    InitializeProvisioningMode(desktop);
                    break;

                case StartupMode.Normal:
                default:
                    InitializeNormalMode(desktop);
                    break;
            }

            // Handle clean-up when desktop lifetime exits
            desktop.Exit += (_, _) => _trayIconManager?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeDecommissionedMode(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Dead-end window — no tray icon, no background services
        var window = new DecommissionedWindow();
        desktop.MainWindow = window;
        // Allow exit when this window closes
        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
    }

    private void InitializeProvisioningMode(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var provisioningWindow = new ProvisioningWindow();
        desktop.MainWindow = provisioningWindow;

        provisioningWindow.RegistrationCompleted += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Transition to normal operational mode
                var mainWindow = new MainWindow();
                desktop.MainWindow = mainWindow;
                mainWindow.Show();

                // Set up tray icon
                SetupTrayIcon(desktop, mainWindow);

                // Close the provisioning window
                provisioningWindow.Close();
            });
        };
    }

    private void InitializeNormalMode(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var mainWindow = new MainWindow();
        desktop.MainWindow = mainWindow;
        SetupTrayIcon(desktop, mainWindow);
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow mainWindow)
    {
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

        _trayIconManager.CheckForUpdatesRequested += async (_, _) =>
        {
            var updateService = services?.GetService<IUpdateService>();
            if (updateService is null)
            {
                logger.LogWarning("Update service not available");
                return;
            }

            logger.LogInformation("Manual update check triggered from tray");
            var result = await updateService.CheckForUpdatesAsync();
            if (result.UpdateAvailable && result.Downloaded)
                logger.LogInformation("Update {Version} downloaded — restart to apply", result.AvailableVersion);
            else if (result.ErrorMessage is not null)
                logger.LogInformation("Update check: {Message}", result.ErrorMessage);
            else
                logger.LogInformation("No updates available");
        };

        _trayIconManager.RestartAgentRequested += (_, _) =>
        {
            logger.LogWarning("Agent restart requested via tray — not yet implemented (DEA-2.x)");
        };

        _trayIconManager.ExitRequested += (_, _) =>
        {
            mainWindow.ForceClose();
            desktop.Shutdown();
        };
    }
}
