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
/// Manages startup role determination and role transition validation.
/// </summary>
public interface IRecoveryManager
{
    /// <summary>
    /// Determine this agent's startup role by reading persisted epoch, probing peers, and resolving.
    /// Returns the role string (PRIMARY, STANDBY_HOT, RECOVERING).
    /// </summary>
    Task<string> DetermineStartupRoleAsync(CancellationToken ct);

    /// <summary>
    /// Check whether this agent can safely transition to STANDBY_HOT.
    /// </summary>
    bool CanTransitionToStandby(ReplicationStateRecord? replState, AgentConfiguration config);
}

public sealed class RecoveryManager : IRecoveryManager
{
    private readonly IPeerCoordinator _coordinator;
    private readonly IPeerHttpClient _peerClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<RecoveryManager> _logger;

    public RecoveryManager(
        IPeerCoordinator coordinator,
        IPeerHttpClient peerClient,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentConfiguration> config,
        IAuditLogger auditLogger,
        ILogger<RecoveryManager> logger)
    {
        _coordinator = coordinator;
        _peerClient = peerClient;
        _scopeFactory = scopeFactory;
        _config = config;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public async Task<string> DetermineStartupRoleAsync(CancellationToken ct)
    {
        var cfg = _config.CurrentValue;

        if (!cfg.SiteHaEnabled)
        {
            _logger.LogInformation("Site HA disabled — starting as PRIMARY (single-agent mode)");
            return "PRIMARY";
        }

        await _auditLogger.LogEventAsync(ReplicationAuditEvents.RECOVERY_STARTED,
            $"capability={cfg.RoleCapability} priority={cfg.SiteHaPriority}", ct: ct);

        // Read persisted epoch
        long persistedEpoch = 0;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
            var replState = await db.ReplicationStates.FindAsync(new object[] { 1 }, ct);
            if (replState is not null)
            {
                persistedEpoch = replState.PrimaryEpoch;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read persisted replication state");
        }

        _coordinator.LocalEpoch = persistedEpoch;

        // Probe all known peers
        var peers = _coordinator.GetPeerStates();
        PeerHealthResponse? activePrimary = null;
        string? activePrimaryId = null;

        foreach (var (agentId, peer) in peers)
        {
            if (string.IsNullOrEmpty(peer.PeerApiBaseUrl))
                continue;

            try
            {
                var health = await _peerClient.GetHealthAsync(peer.PeerApiBaseUrl, ct);
                if (health is not null && health.CurrentRole == "PRIMARY")
                {
                    activePrimary = health;
                    activePrimaryId = agentId;
                    _logger.LogInformation("Found active primary: {AgentId} at epoch={Epoch}",
                        agentId, health.LeaderEpoch);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Peer {AgentId} unreachable during startup probe", agentId);
            }
        }

        string role;

        if (activePrimary is not null)
        {
            // An active PRIMARY exists — never auto-preempt
            if (activePrimary.LeaderEpoch >= persistedEpoch)
            {
                _coordinator.LocalEpoch = activePrimary.LeaderEpoch;
                role = cfg.RoleCapability == "READ_ONLY" ? "READ_ONLY" : "STANDBY_HOT";
                _logger.LogInformation(
                    "Active primary {PrimaryId} has epoch={PrimaryEpoch} >= local={LocalEpoch} — starting as {Role}",
                    activePrimaryId, activePrimary.LeaderEpoch, persistedEpoch, role);
            }
            else
            {
                // Our epoch is higher, but we still defer to the running primary
                role = "STANDBY_HOT";
                _logger.LogWarning(
                    "Local epoch {LocalEpoch} > primary epoch {PrimaryEpoch} but deferring to running primary {PrimaryId}",
                    persistedEpoch, activePrimary.LeaderEpoch, activePrimaryId);
            }
        }
        else
        {
            // No active primary found
            if (cfg.RoleCapability == "PRIMARY_ELIGIBLE")
            {
                role = "RECOVERING";
                _logger.LogInformation("No active primary found — starting as RECOVERING (will attempt election)");
            }
            else
            {
                role = cfg.RoleCapability == "READ_ONLY" ? "READ_ONLY" : "STANDBY_HOT";
                _logger.LogInformation("No active primary found but capability={Capability} — starting as {Role}",
                    cfg.RoleCapability, role);
            }
        }

        await _auditLogger.LogEventAsync(ReplicationAuditEvents.RECOVERY_COMPLETE,
            $"role={role} epoch={_coordinator.LocalEpoch}", ct: ct);

        return role;
    }

    public bool CanTransitionToStandby(ReplicationStateRecord? replState, AgentConfiguration config)
    {
        if (replState is null)
            return false;

        if (!config.SiteHaEnabled)
            return false;

        if (!replState.SnapshotComplete)
            return false;

        var readiness = StandbyReadinessGate.ComputeReadiness(replState, config, DateTimeOffset.UtcNow);
        return readiness != StandbyReadiness.BLOCKED;
    }
}
