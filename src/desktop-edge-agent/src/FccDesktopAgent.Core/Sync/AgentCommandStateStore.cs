using System.Text.Json;
using FccDesktopAgent.Core.Buffer;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Sync;

public enum PendingAgentAction
{
    None,
    ResetLocalState,
    Decommission
}

public enum OperatorNoticeKind
{
    None,
    ReprovisioningRequired,
    ResetInProgress,
    ResetCompleted,
    Decommissioned
}

public sealed class AgentCommandRuntimeState
{
    public PendingAgentAction PendingAction { get; set; }
    public Guid? PendingCommandId { get; set; }
    public bool PendingActionAcked { get; set; }
    public OperatorNoticeKind NoticeKind { get; set; }
    public string? NoticeMessage { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public AgentCommandRuntimeState Clone() => (AgentCommandRuntimeState)MemberwiseClone();
}

public interface IAgentCommandStateStore
{
    AgentCommandRuntimeState Load();
    Task TrackPendingActionAsync(PendingAgentAction action, Guid commandId, CancellationToken ct = default);
    Task MarkPendingActionAckedAsync(PendingAgentAction action, Guid commandId, CancellationToken ct = default);
    Task ClearPendingActionAsync(CancellationToken ct = default);
    Task SetNoticeAsync(OperatorNoticeKind kind, string? message, CancellationToken ct = default);
    Task ClearNoticeAsync(CancellationToken ct = default);
    event EventHandler<AgentCommandRuntimeState>? StateChanged;
}

public sealed class AgentCommandStateStore : IAgentCommandStateStore
{
    private const string FileName = "agent-command-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<AgentCommandStateStore> _logger;
    private readonly string? _baseDirectoryOverride;
    private readonly object _lock = new();
    private AgentCommandRuntimeState? _cached;

    public AgentCommandStateStore(ILogger<AgentCommandStateStore> logger)
    {
        _logger = logger;
    }

    internal AgentCommandStateStore(ILogger<AgentCommandStateStore> logger, string baseDirectory)
    {
        _logger = logger;
        _baseDirectoryOverride = baseDirectory;
    }

    public event EventHandler<AgentCommandRuntimeState>? StateChanged;

    public AgentCommandRuntimeState Load()
    {
        lock (_lock)
        {
            if (_cached is not null)
                return _cached.Clone();
        }

        var path = GetFilePath();
        if (!File.Exists(path))
        {
            var defaultState = new AgentCommandRuntimeState();
            lock (_lock) _cached = defaultState;
            return defaultState.Clone();
        }

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<AgentCommandRuntimeState>(json, JsonOptions)
                        ?? new AgentCommandRuntimeState();
            lock (_lock) _cached = state;
            return state.Clone();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed to read agent-command-state.json — using defaults");
            var fallback = new AgentCommandRuntimeState();
            lock (_lock) _cached = fallback;
            return fallback.Clone();
        }
    }

    public async Task TrackPendingActionAsync(PendingAgentAction action, Guid commandId, CancellationToken ct = default)
    {
        var state = Load();
        state.PendingAction = action;
        state.PendingCommandId = commandId;
        state.PendingActionAcked = false;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await SaveAsync(state, ct);
    }

    public async Task MarkPendingActionAckedAsync(PendingAgentAction action, Guid commandId, CancellationToken ct = default)
    {
        var state = Load();
        if (state.PendingAction != action || state.PendingCommandId != commandId)
            return;

        state.PendingActionAcked = true;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await SaveAsync(state, ct);
    }

    public async Task ClearPendingActionAsync(CancellationToken ct = default)
    {
        var state = Load();
        state.PendingAction = PendingAgentAction.None;
        state.PendingCommandId = null;
        state.PendingActionAcked = false;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await SaveAsync(state, ct);
    }

    public async Task SetNoticeAsync(OperatorNoticeKind kind, string? message, CancellationToken ct = default)
    {
        var state = Load();
        state.NoticeKind = kind;
        state.NoticeMessage = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await SaveAsync(state, ct);
    }

    public async Task ClearNoticeAsync(CancellationToken ct = default)
    {
        var state = Load();
        state.NoticeKind = OperatorNoticeKind.None;
        state.NoticeMessage = null;
        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await SaveAsync(state, ct);
    }

    private async Task SaveAsync(AgentCommandRuntimeState state, CancellationToken ct)
    {
        var path = GetFilePath();
        var tmpPath = path + ".tmp";
        var json = JsonSerializer.Serialize(state, JsonOptions);

        await File.WriteAllTextAsync(tmpPath, json, ct);

        if (File.Exists(path))
            File.Replace(tmpPath, path, destinationBackupFileName: null);
        else
            File.Move(tmpPath, path);

        AgentCommandRuntimeState snapshot;
        lock (_lock)
        {
            _cached = state;
            snapshot = _cached.Clone();
        }

        StateChanged?.Invoke(this, snapshot);
    }

    private string GetFilePath() =>
        Path.Combine(_baseDirectoryOverride ?? AgentDataDirectory.Resolve(), FileName);
}
