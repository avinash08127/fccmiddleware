using System.Windows.Input;
using FccDesktopAgent.Core.Config;

namespace FccDesktopAgent.App.ViewModels;

/// <summary>
/// MVVM ViewModel for the FCC local override settings panel.
/// Binds to editable fields (FCC Host/IP, Port, JPL Port, WebSocket Port)
/// and exposes Save and Reset commands.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly LocalOverrideManager _overrideManager;
    private readonly AgentConfiguration _agentConfig;

    private string _fccHost = string.Empty;
    private string _fccPort = string.Empty;
    private string _jplPort = string.Empty;
    private string _wsPort = string.Empty;
    private string _cloudFccHost = string.Empty;
    private string _cloudFccPort = string.Empty;
    private bool _hasOverrides;
    private string _feedback = string.Empty;

    public SettingsViewModel(LocalOverrideManager overrideManager, AgentConfiguration agentConfig)
    {
        _overrideManager = overrideManager;
        _agentConfig = agentConfig;

        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);

        LoadValues();
    }

    // ── Bindable Properties ─────────────────────────────────────────────────

    public string FccHost
    {
        get => _fccHost;
        set => SetProperty(ref _fccHost, value);
    }

    public string FccPort
    {
        get => _fccPort;
        set => SetProperty(ref _fccPort, value);
    }

    public string JplPort
    {
        get => _jplPort;
        set => SetProperty(ref _jplPort, value);
    }

    public string WsPort
    {
        get => _wsPort;
        set => SetProperty(ref _wsPort, value);
    }

    public string CloudFccHost
    {
        get => _cloudFccHost;
        set => SetProperty(ref _cloudFccHost, value);
    }

    public string CloudFccPort
    {
        get => _cloudFccPort;
        set => SetProperty(ref _cloudFccPort, value);
    }

    public bool HasOverrides
    {
        get => _hasOverrides;
        set => SetProperty(ref _hasOverrides, value);
    }

    public string Feedback
    {
        get => _feedback;
        set => SetProperty(ref _feedback, value);
    }

    // ── Commands ────────────────────────────────────────────────────────────

    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }

    /// <summary>Raised after Save or Reset completes, signalling the adapter should reconnect.</summary>
    public event EventHandler? ReconnectRequested;

    // ── Actions ─────────────────────────────────────────────────────────────

    private void Save()
    {
        try
        {
            int? fccPort = string.IsNullOrWhiteSpace(FccPort) ? null : int.Parse(FccPort);
            int? jplPort = string.IsNullOrWhiteSpace(JplPort) ? null : int.Parse(JplPort);
            int? wsPort = string.IsNullOrWhiteSpace(WsPort) ? null : int.Parse(WsPort);
            string? fccHost = string.IsNullOrWhiteSpace(FccHost) ? null : FccHost.Trim();

            _overrideManager.SaveAll(fccHost, fccPort, jplPort, wsPort);
            HasOverrides = _overrideManager.HasOverrides();
            Feedback = "Overrides saved. Reconnecting...";
            ReconnectRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Feedback = $"Error: {ex.Message}";
        }
    }

    private void Reset()
    {
        _overrideManager.ClearAllOverrides();
        LoadValues();
        HasOverrides = false;
        Feedback = "Overrides cleared. Using cloud defaults. Reconnecting...";
        ReconnectRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LoadValues()
    {
        // Resolve cloud defaults from the current base URL
        if (Uri.TryCreate(_agentConfig.FccBaseUrl, UriKind.Absolute, out var uri))
        {
            CloudFccHost = uri.Host;
            CloudFccPort = uri.Port.ToString();
        }

        // Populate fields with override values (or empty to show placeholders)
        FccHost = _overrideManager.FccHost ?? string.Empty;
        FccPort = _overrideManager.FccPort?.ToString() ?? string.Empty;
        JplPort = _overrideManager.JplPort?.ToString() ?? string.Empty;
        WsPort = _overrideManager.WsPort?.ToString() ?? string.Empty;
        HasOverrides = _overrideManager.HasOverrides();
        Feedback = string.Empty;
    }

    // ── Minimal ICommand implementation ─────────────────────────────────────

    private sealed class RelayCommand(Action execute) : ICommand
    {
#pragma warning disable CS0067 // Event is required by ICommand interface
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
