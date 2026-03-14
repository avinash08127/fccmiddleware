using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FccDesktopAgent.App.ViewModels;

/// <summary>
/// ViewModel for the main dashboard window. Owns status bar state
/// (connectivity, buffer depth, last sync) and navigation command.
/// Created as part of the T-DSK-001 fix to replace direct code-behind control manipulation.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly IServiceProvider? _services;
    private readonly IConnectivityMonitor? _connectivity;
    private readonly IAgentCommandStateStore? _commandStateStore;
    private readonly Timer _statusTimer;
    private readonly CancellationTokenSource _disposeCts = new();

    private string _connectivityText = "Online";
    private IBrush _connectivityDotBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private string _bufferText = "0 pending";
    private string _lastSyncText = "Last sync: Never";
    private string _operatorNoticeText = string.Empty;
    private bool _hasOperatorNotice;
    private IBrush _operatorNoticeBackgroundBrush = new SolidColorBrush(Color.Parse("#FEF3C7"));
    private IBrush _operatorNoticeForegroundBrush = new SolidColorBrush(Color.Parse("#92400E"));
    private string _currentNavTarget = "Dashboard";

    public MainWindowViewModel(IServiceProvider? services)
    {
        _services = services;
        _connectivity = services?.GetService<IConnectivityMonitor>();
        _commandStateStore = services?.GetService<IAgentCommandStateStore>();

        NavigateCommand = new RelayCommand<string>(target =>
            CurrentNavTarget = target ?? "Dashboard");

        if (_connectivity is not null)
        {
            _connectivity.StateChanged += OnConnectivityChanged;
            UpdateConnectivity(_connectivity.Current);
        }

        if (_commandStateStore is not null)
        {
            _commandStateStore.StateChanged += OnOperatorStateChanged;
            UpdateOperatorNotice(_commandStateStore.Load());
        }

        _statusTimer = new Timer(
            _ => _ = RefreshStatusBarAsync(),
            null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    // ── Status Bar Properties ────────────────────────────────────────────────

    public string ConnectivityText
    {
        get => _connectivityText;
        private set => SetProperty(ref _connectivityText, value);
    }

    public IBrush ConnectivityDotBrush
    {
        get => _connectivityDotBrush;
        private set => SetProperty(ref _connectivityDotBrush, value);
    }

    public string BufferText
    {
        get => _bufferText;
        private set => SetProperty(ref _bufferText, value);
    }

    public string LastSyncText
    {
        get => _lastSyncText;
        private set => SetProperty(ref _lastSyncText, value);
    }

    public bool HasOperatorNotice
    {
        get => _hasOperatorNotice;
        private set => SetProperty(ref _hasOperatorNotice, value);
    }

    public string OperatorNoticeText
    {
        get => _operatorNoticeText;
        private set => SetProperty(ref _operatorNoticeText, value);
    }

    public IBrush OperatorNoticeBackgroundBrush
    {
        get => _operatorNoticeBackgroundBrush;
        private set => SetProperty(ref _operatorNoticeBackgroundBrush, value);
    }

    public IBrush OperatorNoticeForegroundBrush
    {
        get => _operatorNoticeForegroundBrush;
        private set => SetProperty(ref _operatorNoticeForegroundBrush, value);
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    public string CurrentNavTarget
    {
        get => _currentNavTarget;
        set => SetProperty(ref _currentNavTarget, value);
    }

    public ICommand NavigateCommand { get; }

    // ── Connectivity ─────────────────────────────────────────────────────────

    private void OnConnectivityChanged(object? sender, ConnectivitySnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() => UpdateConnectivity(snapshot));
    }

    private void UpdateConnectivity(ConnectivitySnapshot snapshot)
    {
        var (text, color) = snapshot.State switch
        {
            ConnectivityState.FullyOnline => ("Online", "#22C55E"),
            ConnectivityState.InternetDown => ("Internet Down", "#EAB308"),
            ConnectivityState.FccUnreachable => ("FCC Unreachable", "#EAB308"),
            ConnectivityState.FullyOffline => ("Offline", "#EF4444"),
            _ => ("Unknown", "#888888")
        };

        ConnectivityText = text;
        ConnectivityDotBrush = new SolidColorBrush(Color.Parse(color));
    }

    private void OnOperatorStateChanged(object? sender, AgentCommandRuntimeState state)
    {
        Dispatcher.UIThread.Post(() => UpdateOperatorNotice(state));
    }

    private void UpdateOperatorNotice(AgentCommandRuntimeState state)
    {
        HasOperatorNotice = state.NoticeKind != OperatorNoticeKind.None
            && !string.IsNullOrWhiteSpace(state.NoticeMessage);
        OperatorNoticeText = state.NoticeMessage ?? string.Empty;

        var (background, foreground) = state.NoticeKind switch
        {
            OperatorNoticeKind.Decommissioned => ("#FEE2E2", "#991B1B"),
            OperatorNoticeKind.ResetCompleted => ("#DBEAFE", "#1D4ED8"),
            _ => ("#FEF3C7", "#92400E")
        };

        OperatorNoticeBackgroundBrush = new SolidColorBrush(Color.Parse(background));
        OperatorNoticeForegroundBrush = new SolidColorBrush(Color.Parse(foreground));
    }

    // ── Buffer Stats ─────────────────────────────────────────────────────────

    private async Task RefreshStatusBarAsync()
    {
        if (_services is null || _disposeCts.IsCancellationRequested) return;

        try
        {
            using var scope = _services.CreateScope();
            var buffer = scope.ServiceProvider.GetService<TransactionBufferManager>();
            if (buffer is null) return;

            var stats = await buffer.GetBufferStatsAsync();

            // F-DSK-002: Read actual last upload time from SyncStateRecord
            DateTimeOffset? lastUploadAt = null;
            var db = scope.ServiceProvider.GetService<AgentDbContext>();
            if (db is not null)
            {
                var syncState = await db.SyncStates.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == 1);
                lastUploadAt = syncState?.LastUploadAt;
            }

            Dispatcher.UIThread.Post(() =>
            {
                BufferText = $"{stats.Pending:N0} pending";

                if (lastUploadAt.HasValue)
                {
                    var elapsed = DateTimeOffset.UtcNow - lastUploadAt.Value;
                    var ago = elapsed.TotalMinutes < 1 ? "Just now"
                        : elapsed.TotalMinutes < 60 ? $"{(int)elapsed.TotalMinutes}m ago"
                        : elapsed.TotalHours < 24 ? $"{(int)elapsed.TotalHours}h ago"
                        : $"{(int)elapsed.TotalDays}d ago";
                    LastSyncText = $"Last sync: {ago}";
                }
                else
                {
                    LastSyncText = "Last sync: Never";
                }
            });
        }
        catch
        {
            // Non-fatal
        }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        if (_connectivity is not null)
            _connectivity.StateChanged -= OnConnectivityChanged;
        if (_commandStateStore is not null)
            _commandStateStore.StateChanged -= OnOperatorStateChanged;
        _statusTimer.Dispose();
        _disposeCts.Dispose();
    }
}
