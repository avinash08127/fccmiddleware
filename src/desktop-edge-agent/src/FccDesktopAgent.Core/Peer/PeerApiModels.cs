namespace FccDesktopAgent.Core.Peer;

// ── Heartbeat ───────────────────────────────────────────────────────────────

public sealed class PeerHeartbeatRequest
{
    public string AgentId { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public string CurrentRole { get; set; } = string.Empty;
    public long LeaderEpoch { get; set; }
    public string? LeaderAgentId { get; set; }
    public string? ConfigVersion { get; set; }
    public double ReplicationLagSeconds { get; set; }
    public long LastSequenceApplied { get; set; }
    public string DeviceClass { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public long UptimeSeconds { get; set; }
    public DateTimeOffset SentAtUtc { get; set; }
}

public sealed class PeerHeartbeatResponse
{
    public string AgentId { get; set; } = string.Empty;
    public string CurrentRole { get; set; } = string.Empty;
    public long LeaderEpoch { get; set; }
    public bool Accepted { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

// ── Health ──────────────────────────────────────────────────────────────────

public sealed class PeerHealthResponse
{
    public string AgentId { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public string CurrentRole { get; set; } = string.Empty;
    public long LeaderEpoch { get; set; }
    public bool FccReachable { get; set; }
    public long UptimeSeconds { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public long HighWaterMarkSeq { get; set; }
    public DateTimeOffset ReportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

// ── Leadership Claim ────────────────────────────────────────────────────────

public sealed class PeerLeadershipClaimRequest
{
    public string CandidateAgentId { get; set; } = string.Empty;
    public long ProposedEpoch { get; set; }
    public int Priority { get; set; }
    public string SiteCode { get; set; } = string.Empty;
}

public sealed class PeerLeadershipClaimResponse
{
    public bool Accepted { get; set; }
    public string? Reason { get; set; }
    public long CurrentEpoch { get; set; }
}

// ── Pre-Auth Proxy ──────────────────────────────────────────────────────────

public sealed class PeerProxyPreAuthRequest
{
    public int PumpNumber { get; set; }
    public int NozzleNumber { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public long RequestedAmount { get; set; }
    public long UnitPrice { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string OdooOrderId { get; set; } = string.Empty;
    public string? VehicleNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerTaxId { get; set; }
    public string? CustomerBusinessName { get; set; }
    public string? AttendantId { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class PeerProxyPreAuthResponse
{
    public bool Success { get; set; }
    public string? PreAuthId { get; set; }
    public string? FccCorrelationId { get; set; }
    public string? FccAuthorizationCode { get; set; }
    public string? FailureReason { get; set; }
    public string Status { get; set; } = string.Empty;
}

// ── Pump Status Proxy ───────────────────────────────────────────────────────

public sealed class PeerProxyPumpStatusResponse
{
    public List<PeerPumpStatus> Pumps { get; set; } = [];
}

public sealed class PeerPumpStatus
{
    public int PumpNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? CurrentNozzle { get; set; }
    public string? CurrentProductCode { get; set; }
}
