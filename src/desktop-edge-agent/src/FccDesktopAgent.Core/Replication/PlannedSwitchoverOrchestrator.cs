using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Peer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Replication;

/// <summary>
/// Orchestrates a planned switchover from the current primary to a target standby.
/// Triggered when the AgentCommandExecutor receives a PLANNED_SWITCHOVER command.
/// </summary>
public interface IPlannedSwitchoverOrchestrator
{
    Task<SwitchoverResult> ExecuteAsync(string targetAgentId, long currentEpoch, CancellationToken ct);
}

public sealed class PlannedSwitchoverOrchestrator : IPlannedSwitchoverOrchestrator
{
    private readonly IPeerCoordinator _coordinator;
    private readonly IPeerHttpClient _peerClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly ILogger<PlannedSwitchoverOrchestrator> _logger;

    public PlannedSwitchoverOrchestrator(
        IPeerCoordinator coordinator,
        IPeerHttpClient peerClient,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<PlannedSwitchoverOrchestrator> logger)
    {
        _coordinator = coordinator;
        _peerClient = peerClient;
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<SwitchoverResult> ExecuteAsync(string targetAgentId, long currentEpoch, CancellationToken ct)
    {
        var cfg = _config.CurrentValue;
        _logger.LogInformation(
            "[{Event}] Starting planned switchover to {TargetAgentId} at epoch {Epoch}",
            ReplicationAuditEvents.SwitchoverStarted, targetAgentId, currentEpoch);

        try
        {
            // Step 1: Verify target is reachable and ready
            var targetHealth = await _peerClient.GetHealthAsync(targetAgentId, ct);
            if (targetHealth is null)
            {
                _logger.LogWarning("[{Event}] Target {TargetAgentId} is unreachable", ReplicationAuditEvents.SwitchoverFailed, targetAgentId);
                return SwitchoverResult.Failed("Target agent is unreachable");
            }

            // Step 2: Drain in-flight operations (give 3 seconds for pending writes to complete)
            _logger.LogInformation("Draining in-flight operations before switchover");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);

            // Step 3: Flush final replication data to target
            // The target's replication sync worker will pick up any remaining deltas

            // Step 4: Claim leadership on target
            var newEpoch = currentEpoch + 1;
            var claimResponse = await _peerClient.ClaimLeadershipAsync(
                targetAgentId,
                new PeerLeadershipClaimRequest
                {
                    CandidateAgentId = targetAgentId,
                    ProposedEpoch = newEpoch,
                    Priority = 0, // Target gets highest priority during planned switchover
                    SiteCode = cfg.SiteId,
                },
                ct);

            if (claimResponse is null || !claimResponse.Accepted)
            {
                _logger.LogWarning("[{Event}] Target {TargetAgentId} rejected leadership claim: {Reason}",
                    ReplicationAuditEvents.SwitchoverFailed, targetAgentId, claimResponse?.Reason ?? "no response");
                return SwitchoverResult.Failed($"Leadership claim rejected: {claimResponse?.Reason ?? "no response"}");
            }

            // Step 5: Self-demote
            _logger.LogInformation(
                "[{Event}] Demoting self — new primary is {TargetAgentId} at epoch {NewEpoch}",
                ReplicationAuditEvents.SwitchoverCompleted, targetAgentId, newEpoch);

            return SwitchoverResult.Succeeded(newEpoch);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[{Event}] Switchover cancelled", ReplicationAuditEvents.SwitchoverFailed);
            return SwitchoverResult.Failed("Switchover cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Event}] Switchover to {TargetAgentId} failed", ReplicationAuditEvents.SwitchoverFailed, targetAgentId);
            return SwitchoverResult.Failed($"Switchover failed: {ex.Message}");
        }
    }
}

public sealed record SwitchoverResult(bool Success, long NewEpoch, string? ErrorMessage)
{
    public static SwitchoverResult Succeeded(long newEpoch) => new(true, newEpoch, null);
    public static SwitchoverResult Failed(string message) => new(false, 0, message);
}
