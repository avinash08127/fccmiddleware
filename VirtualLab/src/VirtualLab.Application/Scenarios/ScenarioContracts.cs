using VirtualLab.Domain.Enums;

namespace VirtualLab.Application.Scenarios;

public sealed class ScenarioScriptDefinition
{
    public int Version { get; init; } = 1;
    public string SiteCode { get; init; } = string.Empty;
    public ScenarioSetupDefinition Setup { get; init; } = new();
    public IReadOnlyList<ScenarioActionDefinition> Actions { get; init; } = [];
    public IReadOnlyList<ScenarioAssertionDefinition> Assertions { get; init; } = [];
}

public sealed class ScenarioSetupDefinition
{
    public bool ResetNozzles { get; init; } = true;
    public bool ClearActivePreAuth { get; init; } = true;
    public string? ProfileKey { get; init; }
    public TransactionDeliveryMode? DeliveryMode { get; init; }
    public PreAuthFlowMode? PreAuthMode { get; init; }
    /// <summary>Target FCC vendor protocol for this scenario (advisory, not routing).</summary>
    public string? FccVendor { get; init; }
}

public sealed class ScenarioActionDefinition
{
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? CorrelationAlias { get; init; }
    public string? CorrelationId { get; init; }
    public int? PumpNumber { get; init; }
    public int? NozzleNumber { get; init; }
    public string? Action { get; init; }
    public decimal? Amount { get; init; }
    public decimal? TargetAmount { get; init; }
    public decimal? TargetVolume { get; init; }
    public decimal? FlowRateLitresPerMinute { get; init; }
    public int? ElapsedSeconds { get; init; }
    public int? ExpiresInSeconds { get; init; }
    public int? DelayMs { get; init; }
    public int? Limit { get; init; }
    public bool InjectDuplicate { get; init; }
    public bool SimulateFailure { get; init; }
    public bool ClearFault { get; init; }
    public string? FailureMessage { get; init; }
    public string? FailureCode { get; init; }
    public int? FailureStatusCode { get; init; }
    public string? TargetKey { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerTaxId { get; init; }
    public string? CustomerTaxOffice { get; init; }
}

public sealed class ScenarioAssertionDefinition
{
    public string Kind { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? CorrelationAlias { get; init; }
    public string? TargetKey { get; init; }
    public string? ExpectedStatus { get; init; }
    public string? Category { get; init; }
    public string? EventType { get; init; }
    public int? ExpectedCount { get; init; }
    public int? MinimumCount { get; init; }
    public bool? IsReplay { get; init; }
}

public sealed record ScenarioDefinitionView(
    Guid Id,
    Guid LabEnvironmentId,
    string ScenarioKey,
    string Name,
    string Description,
    int DeterministicSeed,
    string ReplaySignature,
    bool IsActive,
    ScenarioScriptDefinition Script,
    ScenarioRunSummaryView? LatestRun);

public sealed record ScenarioRunSummaryView(
    Guid Id,
    Guid SiteId,
    Guid ScenarioDefinitionId,
    string ScenarioKey,
    string ScenarioName,
    string SiteCode,
    string CorrelationId,
    int ReplaySeed,
    string ReplaySignature,
    string OutputSignature,
    ScenarioRunStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int StepCount,
    int AssertionCount,
    int ErrorCount);

public sealed record ScenarioStepResultView(
    int Order,
    string Kind,
    string Name,
    string Status,
    string CorrelationId,
    string Message,
    string OutputJson,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

public sealed record ScenarioAssertionResultView(
    int Order,
    string Kind,
    string Name,
    bool Passed,
    string Message,
    string OutputJson);

public sealed record ScenarioRunDetailView(
    Guid Id,
    Guid SiteId,
    Guid ScenarioDefinitionId,
    string ScenarioKey,
    string ScenarioName,
    string SiteCode,
    string CorrelationId,
    int ReplaySeed,
    string ReplaySignature,
    string OutputSignature,
    ScenarioRunStatus Status,
    string InputSnapshotJson,
    string ResultSummaryJson,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<ScenarioStepResultView> Steps,
    IReadOnlyList<ScenarioAssertionResultView> Assertions);

public sealed class ScenarioRunRequest
{
    public Guid? ScenarioId { get; init; }
    public string? ScenarioKey { get; init; }
    public int? ReplaySeed { get; init; }
}

public sealed class ScenarioImportRequest
{
    public bool ReplaceExisting { get; init; }
    public IReadOnlyList<ScenarioDefinitionImportRecord> Definitions { get; init; } = [];
}

public sealed class ScenarioDefinitionImportRecord
{
    public string ScenarioKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public int DeterministicSeed { get; init; }
    public bool IsActive { get; init; } = true;
    public ScenarioScriptDefinition Script { get; init; } = new();
}

public sealed record ScenarioImportResult(
    int ImportedCount,
    int UpdatedCount,
    int CreatedCount,
    int SkippedCount,
    IReadOnlyList<ScenarioDefinitionView> Definitions);

public interface IScenarioService
{
    Task<IReadOnlyList<ScenarioDefinitionView>> ListScenariosAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScenarioRunSummaryView>> ListRunsAsync(int limit, CancellationToken cancellationToken = default);
    Task<ScenarioRunDetailView?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<ScenarioRunDetailView> RunAsync(ScenarioRunRequest request, CancellationToken cancellationToken = default);
    Task<ScenarioImportResult> ImportAsync(ScenarioImportRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScenarioDefinitionImportRecord>> ExportAsync(CancellationToken cancellationToken = default);
}
