namespace VirtualLab.Domain.Models;

public sealed class ScenarioDefinition
{
    public Guid Id { get; set; }
    public Guid LabEnvironmentId { get; set; }
    public string ScenarioKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int DeterministicSeed { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public string ReplaySignature { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }

    public LabEnvironment LabEnvironment { get; set; } = null!;
    public ICollection<ScenarioRun> Runs { get; set; } = new List<ScenarioRun>();
}
