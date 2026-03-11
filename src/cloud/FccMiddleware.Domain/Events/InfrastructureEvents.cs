namespace FccMiddleware.Domain.Events;

/// <summary>Master data sync completed.</summary>
public sealed class MasterDataSynced : DomainEvent
{
    public override string EventType => "MasterDataSynced";
    public string EntityType { get; init; } = null!;
    public int RecordCount { get; init; }
    public long SyncDurationMs { get; init; }
}

/// <summary>Config changed via portal.</summary>
public sealed class ConfigChanged : DomainEvent
{
    public override string EventType => "ConfigChanged";
    public string ConfigScope { get; init; } = null!;
    public string ScopeId { get; init; } = null!;
    public string[] ChangedFields { get; init; } = [];
    public string ChangedBy { get; init; } = null!;
}
