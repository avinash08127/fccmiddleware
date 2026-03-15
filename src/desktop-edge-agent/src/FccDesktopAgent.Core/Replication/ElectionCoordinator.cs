using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Peer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Replication;

/// <summary>
/// Coordinates leader elections using priority-based backoff and epoch fencing.
/// </summary>
public interface IElectionCoordinator
{
    /// <summary>
    /// Attempt to become PRIMARY through a priority-based election.
    /// Returns true if this agent won the election.
    /// </summary>
    Task<bool> AttemptElectionAsync(CancellationToken ct);

    /// <summary>
    /// Called when an epoch is observed from another agent (heartbeat or claim).
    /// If the observed epoch is higher, this agent demotes itself.
    /// </summary>
    Task OnEpochObservedAsync(long epoch, string sourceAgentId, CancellationToken ct);
}

public sealed class ElectionCoordinator : IElectionCoordinator
{
    private readonly IPeerCoordinator _coordinator;
    private readonly IPeerHttpClient _peerClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<ElectionCoordinator> _logger;

    /// <summary>Settlement period after broadcasting a claim — wait for rejections.</summary>
    private static readonly TimeSpan SettlementPeriod = TimeSpan.FromSeconds(3);

    private static readonly Random Jitter = new();

    public ElectionCoordinator(
        IPeerCoordinator coordinator,
        IPeerHttpClient peerClient,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentConfiguration> config,
        IAuditLogger auditLogger,
        ILogger<ElectionCoordinator> logger)
    {
        _coordinator = coordinator;
        _peerClient = peerClient;
        _scopeFactory = scopeFactory;
        _config = config;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public async Task<bool> AttemptElectionAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue;

        if (!cfg.SiteHaEnabled || !cfg.AutoFailoverEnabled)
        {
            _logger.LogDebug("Election skipped — HA or auto-failover disabled");
            return false;
        }

        if (cfg.RoleCapability == "STANDBY_ONLY" || cfg.RoleCapability == "READ_ONLY")
        {
            _logger.LogDebug("Election skipped — role capability is {Capability}", cfg.RoleCapability);
            return false;
        }

        await _auditLogger.LogEventAsync(ReplicationAuditEvents.ELECTION_STARTED,
            $"priority={cfg.SiteHaPriority}", ct: ct);

        // Priority-based backoff: higher priority (lower number) waits less
        var backoffMs = (cfg.SiteHaPriority * 200) + Jitter.Next(0, 500);
        _logger.LogInformation("Election started — waiting {BackoffMs}ms (priority={Priority})",
            backoffMs, cfg.SiteHaPriority);
        await Task.Delay(backoffMs, ct);

        // Check if any higher-priority healthy peer exists
        var peers = _coordinator.GetPeerStates();
        foreach (var (_, peer) in peers)
        {
            if (peer.SuspectStatus == SuspectStatus.Healthy &&
                peer.CurrentRole == "PRIMARY")
            {
                // A healthy primary exists — abort
                _logger.LogInformation("Aborting election — healthy primary {AgentId} found", peer.AgentId);
                await _auditLogger.LogEventAsync(ReplicationAuditEvents.ELECTION_LOST,
                    $"healthy primary {peer.AgentId} found", ct: ct);
                return false;
            }
        }

        // Increment epoch
        var proposedEpoch = _coordinator.LocalEpoch + 1;
        _coordinator.LocalEpoch = proposedEpoch;

        // Persist epoch locally
        await PersistEpochAsync(proposedEpoch, ct);

        // Broadcast leadership claim to all peers
        var claim = new PeerLeadershipClaimRequest
        {
            CandidateAgentId = cfg.DeviceId,
            ProposedEpoch = proposedEpoch,
            Priority = cfg.SiteHaPriority,
            SiteCode = cfg.SiteId,
        };

        var rejected = false;
        foreach (var (agentId, peer) in peers)
        {
            if (string.IsNullOrEmpty(peer.PeerApiBaseUrl))
                continue;

            try
            {
                var response = await _peerClient.ClaimLeadershipAsync(peer.PeerApiBaseUrl, claim, ct);
                if (response is not null && !response.Accepted)
                {
                    _logger.LogWarning("Leadership claim rejected by {AgentId}: {Reason}",
                        agentId, response.Reason);
                    rejected = true;

                    // If peer has higher epoch, adopt it
                    if (response.CurrentEpoch > proposedEpoch)
                    {
                        _coordinator.LocalEpoch = response.CurrentEpoch;
                        await PersistEpochAsync(response.CurrentEpoch, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send leadership claim to {AgentId}", agentId);
                // Unreachable peers don't reject — they may be down
            }
        }

        if (rejected)
        {
            _logger.LogInformation("Election lost — claim was rejected");
            await _auditLogger.LogEventAsync(ReplicationAuditEvents.ELECTION_LOST,
                $"claim rejected at epoch={proposedEpoch}", ct: ct);
            return false;
        }

        // Wait settlement period for late rejections
        _logger.LogInformation("Waiting {SettlementMs}ms settlement period", SettlementPeriod.TotalMilliseconds);
        await Task.Delay(SettlementPeriod, ct);

        // No rejection received — assume leadership
        _logger.LogInformation("Election won — this agent is now PRIMARY at epoch={Epoch}", proposedEpoch);
        await _auditLogger.LogEventAsync(ReplicationAuditEvents.ELECTION_WON,
            $"epoch={proposedEpoch}", ct: ct);

        return true;
    }

    public async Task OnEpochObservedAsync(long epoch, string sourceAgentId, CancellationToken ct)
    {
        if (epoch <= _coordinator.LocalEpoch)
            return;

        _logger.LogWarning(
            "Higher epoch observed: {ObservedEpoch} from {SourceAgent} (local={LocalEpoch}) — self-demoting",
            epoch, sourceAgentId, _coordinator.LocalEpoch);

        _coordinator.LocalEpoch = epoch;
        await PersistEpochAsync(epoch, ct);

        await _auditLogger.LogEventAsync(ReplicationAuditEvents.SELF_DEMOTION,
            $"epoch={epoch} from={sourceAgentId}", ct: ct);
    }

    private async Task PersistEpochAsync(long epoch, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();

            var replState = await db.ReplicationStates.FindAsync(new object[] { 1 }, ct);
            if (replState is null)
            {
                replState = new ReplicationStateRecord { Id = 1, PrimaryEpoch = epoch };
                db.ReplicationStates.Add(replState);
            }
            else
            {
                replState.PrimaryEpoch = epoch;
                replState.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist epoch {Epoch}", epoch);
        }
    }
}
