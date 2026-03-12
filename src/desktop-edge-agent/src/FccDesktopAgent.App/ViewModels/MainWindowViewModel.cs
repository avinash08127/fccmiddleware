using System.Windows.Input;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Connectivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.App.ViewModels;

/// <summary>
/// Drives the main window: sidebar navigation, connectivity status bar, and active page content.
/// Subscribes to <see cref="IConnectivityMonitor"/> for real-time status bar updates
/// and periodically polls buffer stats.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IConnectivityMonitor? _connectivity;
    private readonly IServiceProvider? _services;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _bufferStatsTimer;

    private string _selectedNav = "Dashboard";
    private string _connectivityText = "Unknown";
    private string _connectivityColor = "#888888";
    private string _connectivityIcon = "\u25CF"; // filled circle
    private int _bufferDepth;
    private string _lastSyncText = "Never";
    private DateTimeOffset? _lastSyncTime;

    public MainWindowViewModel()
    {
        _services = AgentAppContext.ServiceProvider;
        _logger = _services?.GetService<ILogger<MainWindowViewModel>>()
            ?? LoggerFactory.Create(b => b.AddConsole()).CreateLogger<MainWindowViewModel>();
        _connectivity = _services?.GetService<IConnectivityMonitor>();

        NavigateCommand = new RelayCommand<string>(navTarget =>
        {
            if (navTarget is not null)
                SelectedNav = navTarget;
        });

        if (_connectivity is not null)
        {
            _connectivity.StateChanged += OnConnectivityChanged;
            UpdateConnectivityDisplay(_connectivity.Current);
        }

        // Poll buffer stats every 5 seconds
        _bufferStatsTimer = new Timer(_ => _ = RefreshBufferStatsAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public ICommand NavigateCommand { get; }

    public string SelectedNav
    {
        get => _selectedNav;
        set => SetProperty(ref _selectedNav, value);
    }

    public string ConnectivityText
    {
        get => _connectivityText;
        private set => SetProperty(ref _connectivityText, value);
    }

    public string ConnectivityColor
    {
        get => _connectivityColor;
        private set => SetProperty(ref _connectivityColor, value);
    }

    public string ConnectivityIcon
    {
        get => _connectivityIcon;
        private set => SetProperty(ref _connectivityIcon, value);
    }

    public int BufferDepth
    {
        get => _bufferDepth;
        private set => SetProperty(ref _bufferDepth, value);
    }

    public string LastSyncText
    {
        get => _lastSyncText;
        private set => SetProperty(ref _lastSyncText, value);
    }

    private void OnConnectivityChanged(object? sender, ConnectivitySnapshot snapshot)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateConnectivityDisplay(snapshot));
    }

    private void UpdateConnectivityDisplay(ConnectivitySnapshot snapshot)
    {
        (ConnectivityText, ConnectivityColor) = snapshot.State switch
        {
            ConnectivityState.FullyOnline => ("Online", "#22C55E"),          // green
            ConnectivityState.InternetDown => ("Internet Down", "#EAB308"), // yellow
            ConnectivityState.FccUnreachable => ("FCC Unreachable", "#EAB308"),
            ConnectivityState.FullyOffline => ("Offline", "#EF4444"),       // red
            _ => ("Unknown", "#888888")
        };
        ConnectivityIcon = "\u25CF"; // always filled circle, color conveys state
    }

    private async Task RefreshBufferStatsAsync()
    {
        if (_services is null) return;

        try
        {
            using var scope = _services.CreateScope();
            var bufferManager = scope.ServiceProvider.GetService<TransactionBufferManager>();
            if (bufferManager is null) return;

            var stats = await bufferManager.GetBufferStatsAsync(_cts.Token);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                BufferDepth = stats.Pending;

                // Update last sync time based on whether there are uploaded (synced) records
                if (_connectivity?.Current.IsInternetUp == true)
                {
                    _lastSyncTime = DateTimeOffset.UtcNow;
                    LastSyncText = "Just now";
                }
                else if (_lastSyncTime.HasValue)
                {
                    var elapsed = DateTimeOffset.UtcNow - _lastSyncTime.Value;
                    LastSyncText = elapsed.TotalMinutes < 1 ? "Just now"
                        : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
                        : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago"
                        : $"{(int)elapsed.TotalDays}d ago";
                }
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Buffer stats refresh failed");
        }
    }

    public void Dispose()
    {
        if (_connectivity is not null)
            _connectivity.StateChanged -= OnConnectivityChanged;

        _cts.Cancel();
        _bufferStatsTimer.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Simple ICommand relay for navigation.</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;

    public RelayCommand(Action<T?> execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute((T?)parameter);
}
