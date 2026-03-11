namespace FccMiddleware.Domain.Events;

/// <summary>New Edge Agent registered.</summary>
public sealed class AgentRegistered : DomainEvent
{
    public override string EventType => "AgentRegistered";
    public string DeviceId { get; init; } = null!;
    public string AgentVersion { get; init; } = null!;
    public string? HardwareModel { get; init; }
}

/// <summary>Config pushed to agent.</summary>
public sealed class AgentConfigUpdated : DomainEvent
{
    public override string EventType => "AgentConfigUpdated";
    public string DeviceId { get; init; } = null!;
    public int ConfigVersion { get; init; }
    public string[] ChangedFields { get; init; } = [];
}

/// <summary>Telemetry snapshot received.</summary>
public sealed class AgentHealthReported : DomainEvent
{
    public override string EventType => "AgentHealthReported";
    public string DeviceId { get; init; } = null!;
    public int BufferDepth { get; init; }
    public DateTimeOffset? LastFccHeartbeat { get; init; }
    public int SyncLagSeconds { get; init; }
    public int? BatteryPercent { get; init; }
}
