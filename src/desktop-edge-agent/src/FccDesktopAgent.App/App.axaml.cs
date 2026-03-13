using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FccDesktopAgent.App.Views;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Registration;
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

            var splash = new SplashWindow();
            desktop.MainWindow = splash;

            var timer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

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

                splash.Close();

                // Handle clean-up when desktop lifetime exits
                desktop.Exit += (_, _) => _trayIconManager?.Dispose();
            };
            timer.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void InitializeDecommissionedMode(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Dead-end window — no tray icon, no background services
        var window = new DecommissionedWindow();
        desktop.MainWindow = window;
        // M-07: Show the decommissioned window BEFORE changing ShutdownMode.
        // If ShutdownMode is set to OnLastWindowClose before the new window is visible,
        // closing the splash (the only visible window) triggers premature app shutdown.
        window.Show();
        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
    }

    private void InitializeProvisioningMode(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // T-DSK-002: Pass IServiceProvider via constructor instead of static service locator.
        var provisioningWindow = new ProvisioningWindow(AgentAppContext.ServiceProvider);
        desktop.MainWindow = provisioningWindow;

        provisioningWindow.RegistrationCompleted += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Transition to normal operational mode
                var mainWindow = new MainWindow(AgentAppContext.ServiceProvider);
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
        var mainWindow = new MainWindow(AgentAppContext.ServiceProvider);
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
            logger.LogInformation("Agent restart requested via tray");
            try
            {
                var exePath = Environment.ProcessPath;
                if (exePath is null)
                {
                    logger.LogWarning("Cannot restart: unable to determine process path");
                    return;
                }

                // F-DSK-004: Shutdown first, then start the new process.
                // Starting the new process before shutdown causes port conflicts
                // because the old process still holds port 8585 (Kestrel).
                mainWindow.ForceClose();

                // Launch the new process in the desktop.Exit handler so it starts
                // after the old host has released ports and database locks.
                void OnExit(object? s, EventArgs e)
                {
                    desktop.Exit -= OnExit;
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true,
                    });
                }

                desktop.Exit += OnExit;
                desktop.Shutdown();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to restart agent");
            }
        };

        _trayIconManager.ExitRequested += (_, _) =>
        {
            mainWindow.ForceClose();
            desktop.Shutdown();
        };

        // DEA-3.x: Subscribe to decommission events from cloud sync workers.
        // When the cloud returns DEVICE_DECOMMISSIONED, transition the GUI to the dead-end window.
        var registrationManager = services?.GetService<IRegistrationManager>();
        if (registrationManager is not null)
        {
            registrationManager.DeviceDecommissioned += (_, _) =>
            {
                logger.LogWarning("Device decommissioned — transitioning to decommission screen");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _trayIconManager?.Dispose();
                    _trayIconManager = null;

                    var decommissionedWindow = new DecommissionedWindow();
                    desktop.MainWindow = decommissionedWindow;
                    desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                    decommissionedWindow.Show();

                    mainWindow.ForceClose();
                });
            };

            // M-10: Subscribe to re-provisioning events so the user sees a prompt
            // instead of the agent silently halting uploads with no indication.
            registrationManager.ReprovisioningRequired += (_, _) =>
            {
                logger.LogWarning("Re-provisioning required — restarting into provisioning mode");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _trayIconManager?.Dispose();
                    _trayIconManager = null;

                    var provisioningWindow = new ProvisioningWindow(AgentAppContext.ServiceProvider);
                    desktop.MainWindow = provisioningWindow;
                    provisioningWindow.Show();

                    provisioningWindow.RegistrationCompleted += (_, _) =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            var newMainWindow = new MainWindow(AgentAppContext.ServiceProvider);
                            desktop.MainWindow = newMainWindow;
                            newMainWindow.Show();
                            SetupTrayIcon(desktop, newMainWindow);
                            provisioningWindow.Close();
                        });
                    };

                    mainWindow.ForceClose();
                });
            };
        }
    }
}
