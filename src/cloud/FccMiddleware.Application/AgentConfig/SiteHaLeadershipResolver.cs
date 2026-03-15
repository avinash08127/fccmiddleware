using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;

namespace FccMiddleware.Application.AgentConfig;

/// <summary>
/// Computes the active peer set, elected leader, and effective leader epoch for a site.
/// Shared by config, portal, and write-fencing paths so all surfaces use the same ordering rules.
///
/// P2-15: Agent elections are authoritative. When the site has a recorded leader epoch
/// (<see cref="Site.HaLeaderEpoch"/> > 0), the resolver uses the recorded leader and epoch
/// rather than computing them from priority ordering. Priority ordering is only used for
/// initial cold-start assignment when no epoch has been recorded.
/// </summary>
public static class SiteHaLeadershipResolver
{
    /// <summary>
    /// Resolve the leadership snapshot for a site, preferring the recorded leader/epoch
    /// from <paramref name="site"/> when available.
    /// </summary>
    public static SiteHaLeadershipSnapshot ResolveSnapshot(
        IEnumerable<AgentRegistration> siteAgents,
        Site? site = null)
    {
        var activePeers = siteAgents
            .Where(IsActivePeer)
            .OrderBy(agent => agent.SiteHaPriority)
            .ThenBy(agent => agent.RegisteredAt)
            .ToArray();

        // P2-15: If the site has a recorded leader epoch from a prior agent election,
        // use the recorded leader rather than computing from priority ordering.
        if (site is not null && site.HaLeaderEpoch > 0 && site.HaLeaderAgentId.HasValue)
        {
            var recordedLeader = activePeers.FirstOrDefault(a => a.Id == site.HaLeaderAgentId.Value);
            if (recordedLeader is not null)
            {
                return new SiteHaLeadershipSnapshot(activePeers, recordedLeader, site.HaLeaderEpoch);
            }
            // Recorded leader is no longer active — fall through to computed leader
            // but preserve the recorded epoch as minimum
        }

        var leader = DetermineLeader(activePeers);
        var leaderEpoch = DetermineLeaderEpoch(activePeers);

        // If the site's recorded epoch is higher than the computed one, use it
        if (site is not null && site.HaLeaderEpoch > leaderEpoch)
        {
            leaderEpoch = site.HaLeaderEpoch;
        }

        return new SiteHaLeadershipSnapshot(activePeers, leader, leaderEpoch);
    }

    /// <summary>
    /// Overload without site context — uses only computed leader/epoch (backward compatible).
    /// </summary>
    public static SiteHaLeadershipSnapshot ResolveSnapshot(IEnumerable<AgentRegistration> siteAgents)
        => ResolveSnapshot(siteAgents, site: null);

    public static AgentRegistration? DetermineLeader(IEnumerable<AgentRegistration> activePeers) =>
        activePeers
            .Where(agent => !string.Equals(agent.RoleCapability, "READ_ONLY", StringComparison.OrdinalIgnoreCase))
            .OrderBy(agent => agent.SiteHaPriority)
            .ThenBy(agent => string.Equals(agent.DeviceClass, "DESKTOP", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(agent => agent.RegisteredAt)
            .FirstOrDefault();

    public static long DetermineLeaderEpoch(IEnumerable<AgentRegistration> activePeers)
    {
        var peers = activePeers as IReadOnlyCollection<AgentRegistration> ?? activePeers.ToArray();
        return peers.Count == 0
            ? 0
            : Math.Max(1, peers.Max(agent => agent.LeaderEpochSeen ?? 0));
    }

    public static string ResolveCurrentRole(AgentRegistration agent, AgentRegistration? leader)
    {
        if (!agent.IsActive || agent.Status != AgentRegistrationStatus.ACTIVE)
        {
            return "OFFLINE";
        }

        // If the agent has self-reported a runtime role (e.g. CANDIDATE during an
        // in-progress election), respect it rather than overriding with a computed role.
        if (!string.IsNullOrWhiteSpace(agent.CurrentRole))
        {
            return agent.CurrentRole!;
        }

        if (string.Equals(agent.RoleCapability, "READ_ONLY", StringComparison.OrdinalIgnoreCase))
        {
            return "READ_ONLY";
        }

        return leader?.Id == agent.Id ? "PRIMARY" : "STANDBY_HOT";
    }

    private static bool IsActivePeer(AgentRegistration agent) =>
        agent.IsActive && agent.Status == AgentRegistrationStatus.ACTIVE;
}

public sealed record SiteHaLeadershipSnapshot(
    IReadOnlyList<AgentRegistration> ActivePeers,
    AgentRegistration? Leader,
    long LeaderEpoch);
