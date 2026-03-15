using System.Text.Json;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.MasterData;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Sync;

public enum LocalCommandAckOutcome
{
    Succeeded,
    Failed,
    IgnoredAlreadyApplied,
    IgnoredExpired
}

public enum PostAckAction
{
    None,
    FinalizeResetLocalState,
    FinalizeDecommission
}

public sealed class AgentCommandExecutionResult
{
    public required LocalCommandAckOutcome Outcome { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public required JsonElement Result { get; init; }
    public PostAckAction PostAckAction { get; init; } = PostAckAction.None;
}

public interface IAgentCommandExecutor
{
    Task<AgentCommandExecutionResult> ExecuteAsync(
        EdgeCommandItem command,
        DateTimeOffset? serverTimeUtc,
        CancellationToken ct);

    Task<bool> FinalizeAckedActionIfNeededAsync(string origin, CancellationToken ct);

    Task CompletePostAckActionAsync(PostAckAction action, Guid commandId, CancellationToken ct);
}

public sealed class DesktopAgentCommandExecutor : IAgentCommandExecutor
{
    private const string DeviceDecommissionedCode = "DEVICE_DECOMMISSIONED";
    private const string ConfigPullFailedCode = "CONFIG_PULL_FAILED";
    private const string ConfigPullUnavailableCode = "CONFIG_PULL_UNAVAILABLE";

    private readonly ConfigPollWorker _configPollWorker;
    private readonly IConfigManager _configManager;
    private readonly IRegistrationManager _registrationManager;
    private readonly IAgentCommandStateStore _stateStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LocalOverrideManager _overrideManager;
    private readonly SiteDataManager _siteDataManager;
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<DesktopAgentCommandExecutor> _logger;

    public DesktopAgentCommandExecutor(
        ConfigPollWorker configPollWorker,
        IConfigManager configManager,
        IRegistrationManager registrationManager,
        IAgentCommandStateStore stateStore,
        IServiceScopeFactory scopeFactory,
        LocalOverrideManager overrideManager,
        SiteDataManager siteDataManager,
        ICredentialStore credentialStore,
        ILogger<DesktopAgentCommandExecutor> logger)
    {
        _configPollWorker = configPollWorker;
        _configManager = configManager;
        _registrationManager = registrationManager;
        _stateStore = stateStore;
        _scopeFactory = scopeFactory;
        _overrideManager = overrideManager;
        _siteDataManager = siteDataManager;
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task<AgentCommandExecutionResult> ExecuteAsync(
        EdgeCommandItem command,
        DateTimeOffset? serverTimeUtc,
        CancellationToken ct)
    {
        if (IsExpired(command, serverTimeUtc))
            return Ignored(LocalCommandAckOutcome.IgnoredExpired, command);

        return command.CommandType switch
        {
            AgentCommandType.FORCE_CONFIG_PULL => await ExecuteForceConfigPullAsync(command, ct),
            AgentCommandType.RESET_LOCAL_STATE => await ExecuteResetLocalStateAsync(command, ct),
            AgentCommandType.DECOMMISSION => await ExecuteDecommissionAsync(command, ct),
            AgentCommandType.REFRESH_CONFIG => await ExecuteRefreshConfigAsync(command, ct),
            _ => Failure(command, "COMMAND_NOT_SUPPORTED", $"Unsupported command type {command.CommandType}")
        };
    }

    public async Task<bool> FinalizeAckedActionIfNeededAsync(string origin, CancellationToken ct)
    {
        var state = _stateStore.Load();
        if (!state.PendingActionAcked || state.PendingCommandId is null || state.PendingAction == PendingAgentAction.None)
            return false;

        _logger.LogWarning(
            "Finalizing previously acked action {Action} for command {CommandId} (origin={Origin})",
            state.PendingAction, state.PendingCommandId, origin);

        await CompletePendingActionAsync(state.PendingAction, state.PendingCommandId.Value, ct);
        return true;
    }

    public async Task CompletePostAckActionAsync(PostAckAction action, Guid commandId, CancellationToken ct)
    {
        var pendingAction = action switch
        {
            PostAckAction.FinalizeResetLocalState => PendingAgentAction.ResetLocalState,
            PostAckAction.FinalizeDecommission => PendingAgentAction.Decommission,
            _ => PendingAgentAction.None
        };

        if (pendingAction == PendingAgentAction.None)
            return;

        await _stateStore.MarkPendingActionAckedAsync(pendingAction, commandId, ct);
        await CompletePendingActionAsync(pendingAction, commandId, ct);
    }

    private async Task<AgentCommandExecutionResult> ExecuteForceConfigPullAsync(
        EdgeCommandItem command,
        CancellationToken ct)
    {
        var requestedVersion = TryReadRequestedConfigVersion(command.Payload);
        var currentVersion = _configManager.CurrentSiteConfig?.ConfigVersion;
        if (requestedVersion.HasValue && currentVersion.HasValue && currentVersion.Value >= requestedVersion.Value)
        {
            return Ignored(
                LocalCommandAckOutcome.IgnoredAlreadyApplied,
                command,
                new Dictionary<string, object?>
                {
                    ["currentConfigVersion"] = currentVersion.Value,
                    ["requestedConfigVersion"] = requestedVersion.Value,
                });
        }

        var result = await _configPollWorker.PollWithDetailsAsync(ct);
        return result switch
        {
            ConfigPollExecutionResult.Applied applied => Success(
                command,
                new Dictionary<string, object?> { ["appliedConfigVersion"] = applied.ConfigVersion }),

            ConfigPollExecutionResult.Unchanged unchanged => Ignored(
                LocalCommandAckOutcome.IgnoredAlreadyApplied,
                command,
                new Dictionary<string, object?> { ["currentConfigVersion"] = unchanged.CurrentConfigVersion }),

            ConfigPollExecutionResult.Skipped skipped => Ignored(
                LocalCommandAckOutcome.IgnoredAlreadyApplied,
                command,
                new Dictionary<string, object?> { ["currentConfigVersion"] = skipped.ConfigVersion }),

            ConfigPollExecutionResult.Rejected rejected => Failure(
                command,
                rejected.Reason,
                "Config command rejected by local validation",
                new Dictionary<string, object?> { ["configVersion"] = rejected.ConfigVersion }),

            ConfigPollExecutionResult.Decommissioned => Failure(
                command,
                DeviceDecommissionedCode,
                "Device was decommissioned during config pull"),

            ConfigPollExecutionResult.TransportFailure transport => Failure(
                command,
                ConfigPullFailedCode,
                transport.Message),

            ConfigPollExecutionResult.Unavailable unavailable => Failure(
                command,
                ConfigPullUnavailableCode,
                unavailable.Reason),

            _ => Failure(command, ConfigPullFailedCode, "Config pull failed")
        };
    }

    private async Task<AgentCommandExecutionResult> ExecuteRefreshConfigAsync(
        EdgeCommandItem command,
        CancellationToken ct)
    {
        var result = await _configPollWorker.PollWithDetailsAsync(ct);
        return result switch
        {
            ConfigPollExecutionResult.Applied applied => Success(
                command,
                new Dictionary<string, object?> { ["appliedConfigVersion"] = applied.ConfigVersion }),

            ConfigPollExecutionResult.Unchanged unchanged => Success(
                command,
                new Dictionary<string, object?> { ["currentConfigVersion"] = unchanged.CurrentConfigVersion }),

            ConfigPollExecutionResult.Skipped skipped => Success(
                command,
                new Dictionary<string, object?> { ["currentConfigVersion"] = skipped.ConfigVersion }),

            ConfigPollExecutionResult.Rejected rejected => Failure(
                command,
                rejected.Reason,
                "Config refresh rejected by local validation",
                new Dictionary<string, object?> { ["configVersion"] = rejected.ConfigVersion }),

            ConfigPollExecutionResult.Decommissioned => Failure(
                command,
                DeviceDecommissionedCode,
                "Device was decommissioned during config refresh"),

            ConfigPollExecutionResult.TransportFailure transport => Failure(
                command,
                ConfigPullFailedCode,
                transport.Message),

            ConfigPollExecutionResult.Unavailable unavailable => Failure(
                command,
                ConfigPullUnavailableCode,
                unavailable.Reason),

            _ => Failure(command, ConfigPullFailedCode, "Config refresh failed")
        };
    }

    private async Task<AgentCommandExecutionResult> ExecuteResetLocalStateAsync(
        EdgeCommandItem command,
        CancellationToken ct)
    {
        var state = _stateStore.Load();
        if (state.PendingAction == PendingAgentAction.ResetLocalState)
        {
            if (state.PendingCommandId == command.CommandId)
            {
                return Ignored(
                    state.PendingActionAcked
                        ? LocalCommandAckOutcome.IgnoredAlreadyApplied
                        : LocalCommandAckOutcome.Succeeded,
                    command,
                    new Dictionary<string, object?>
                    {
                        ["pendingResetCommandId"] = state.PendingCommandId,
                        ["pendingResetState"] = state.PendingActionAcked ? "acked" : "pending",
                    },
                    state.PendingActionAcked ? PostAckAction.None : PostAckAction.FinalizeResetLocalState);
            }

            return Ignored(
                LocalCommandAckOutcome.IgnoredAlreadyApplied,
                command,
                new Dictionary<string, object?> { ["pendingResetCommandId"] = state.PendingCommandId });
        }

        await _stateStore.TrackPendingActionAsync(PendingAgentAction.ResetLocalState, command.CommandId, ct);
        await _stateStore.SetNoticeAsync(
            OperatorNoticeKind.ResetInProgress,
            "Reset in progress. Waiting for cloud acknowledgement before entering provisioning mode.",
            ct);

        return Success(
            command,
            new Dictionary<string, object?> { ["pendingResetCommandId"] = command.CommandId },
            PostAckAction.FinalizeResetLocalState);
    }

    private Task<AgentCommandExecutionResult> ExecuteDecommissionAsync(
        EdgeCommandItem command,
        CancellationToken ct)
    {
        if (_registrationManager.IsDecommissioned)
        {
            return Task.FromResult(Ignored(LocalCommandAckOutcome.IgnoredAlreadyApplied, command));
        }

        var state = _stateStore.Load();
        if (state.PendingAction == PendingAgentAction.Decommission)
        {
            if (state.PendingCommandId == command.CommandId)
            {
                return Task.FromResult(Ignored(
                    state.PendingActionAcked
                        ? LocalCommandAckOutcome.IgnoredAlreadyApplied
                        : LocalCommandAckOutcome.Succeeded,
                    command,
                    new Dictionary<string, object?>
                    {
                        ["pendingDecommissionCommandId"] = state.PendingCommandId,
                        ["pendingDecommissionState"] = state.PendingActionAcked ? "acked" : "pending",
                    },
                    state.PendingActionAcked ? PostAckAction.None : PostAckAction.FinalizeDecommission));
            }

            return Task.FromResult(Ignored(
                LocalCommandAckOutcome.IgnoredAlreadyApplied,
                command,
                new Dictionary<string, object?> { ["pendingDecommissionCommandId"] = state.PendingCommandId }));
        }

        return TrackDecommissionAsync(command, ct);
    }

    private async Task<AgentCommandExecutionResult> TrackDecommissionAsync(
        EdgeCommandItem command,
        CancellationToken ct)
    {
        await _stateStore.TrackPendingActionAsync(PendingAgentAction.Decommission, command.CommandId, ct);
        return Success(
            command,
            new Dictionary<string, object?> { ["pendingDecommissionCommandId"] = command.CommandId },
            PostAckAction.FinalizeDecommission);
    }

    private async Task CompletePendingActionAsync(PendingAgentAction action, Guid commandId, CancellationToken ct)
    {
        try
        {
            switch (action)
            {
                case PendingAgentAction.ResetLocalState:
                    await FinalizeResetLocalStateAsync(ct);
                    break;

                case PendingAgentAction.Decommission:
                    await FinalizeDecommissionAsync(ct);
                    break;
            }

            await _stateStore.ClearPendingActionAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to complete post-ack action {Action} for command {CommandId}", action, commandId);
            throw;
        }
    }

    private async Task FinalizeResetLocalStateAsync(CancellationToken ct)
    {
        await ClearLocalPersistenceAsync(ct);
        await _registrationManager.MarkReprovisioningRequiredAsync(ct);
        await _stateStore.SetNoticeAsync(
            OperatorNoticeKind.ResetCompleted,
            "Reset completed. Re-provisioning is required before the agent can resume normal operation.",
            ct);
    }

    private async Task FinalizeDecommissionAsync(CancellationToken ct)
    {
        await _registrationManager.MarkDecommissionedAsync(ct);
        await _stateStore.SetNoticeAsync(
            OperatorNoticeKind.Decommissioned,
            "This device has been decommissioned and cannot be returned to service without administrator action.",
            ct);
    }

    private async Task ClearLocalPersistenceAsync(CancellationToken ct)
    {
        await _configManager.ResetAsync(ct);
        _overrideManager.ClearAllOverrides();
        _siteDataManager.Clear();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        db.Transactions.RemoveRange(await db.Transactions.ToListAsync(ct));
        db.PreAuths.RemoveRange(await db.PreAuths.ToListAsync(ct));
        db.NozzleMappings.RemoveRange(await db.NozzleMappings.ToListAsync(ct));
        db.AuditLog.RemoveRange(await db.AuditLog.ToListAsync(ct));

        var syncState = await db.SyncStates.FindAsync([1], ct);
        if (syncState is not null)
        {
            syncState.LastFccSequence = null;
            syncState.LastUploadAt = null;
            syncState.LastStatusSyncAt = null;
            syncState.PendingCount = 0;
            syncState.UploadedCount = 0;
            syncState.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        await DeleteCredentialAsync(CredentialKeys.LanApiKey, ct);
        await DeleteCredentialAsync(CredentialKeys.FccApiKey, ct);
        await DeleteCredentialAsync(CredentialKeys.PetroniteClientSecret, ct);
        await DeleteCredentialAsync(CredentialKeys.DomsFcAccessCode, ct);
        await DeleteCredentialAsync(CredentialKeys.RadixSharedSecret, ct);
    }

    private async Task DeleteCredentialAsync(string key, CancellationToken ct)
    {
        try
        {
            await _credentialStore.DeleteSecretAsync(key, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear credential store key {Key} during local reset", key);
        }
    }

    private static bool IsExpired(EdgeCommandItem command, DateTimeOffset? serverTimeUtc)
    {
        var now = serverTimeUtc ?? DateTimeOffset.UtcNow;
        return command.ExpiresAt <= now;
    }

    private static int? TryReadRequestedConfigVersion(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } json)
            return null;

        if (!json.TryGetProperty("configVersion", out var version))
            return null;

        if (version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var numeric))
            return numeric;

        if (version.ValueKind == JsonValueKind.String && int.TryParse(version.GetString(), out var text))
            return text;

        return null;
    }

    private static AgentCommandExecutionResult Success(
        EdgeCommandItem command,
        IDictionary<string, object?>? extra = null,
        PostAckAction postAckAction = PostAckAction.None)
        => new()
        {
            Outcome = LocalCommandAckOutcome.Succeeded,
            Result = BuildOutcomeJson(command, LocalCommandAckOutcome.Succeeded, extra),
            PostAckAction = postAckAction
        };

    private static AgentCommandExecutionResult Ignored(
        LocalCommandAckOutcome outcome,
        EdgeCommandItem command,
        IDictionary<string, object?>? extra = null,
        PostAckAction postAckAction = PostAckAction.None)
        => new()
        {
            Outcome = outcome,
            Result = BuildOutcomeJson(command, outcome, extra),
            PostAckAction = postAckAction
        };

    private static AgentCommandExecutionResult Failure(
        EdgeCommandItem command,
        string failureCode,
        string failureMessage,
        IDictionary<string, object?>? extra = null)
        => new()
        {
            Outcome = LocalCommandAckOutcome.Failed,
            FailureCode = failureCode,
            FailureMessage = failureMessage,
            Result = BuildOutcomeJson(command, LocalCommandAckOutcome.Failed, extra)
        };

    private static JsonElement BuildOutcomeJson(
        EdgeCommandItem command,
        LocalCommandAckOutcome outcome,
        IDictionary<string, object?>? extra)
    {
        var payload = new Dictionary<string, object?>
        {
            ["commandType"] = command.CommandType.ToString(),
            ["outcome"] = outcome.ToString(),
        };

        if (extra is not null)
        {
            foreach (var entry in extra)
                payload[entry.Key] = entry.Value;
        }

        return JsonSerializer.SerializeToElement(payload);
    }
}
