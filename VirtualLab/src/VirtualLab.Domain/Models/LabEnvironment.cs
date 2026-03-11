namespace VirtualLab.Domain.Models;

public sealed class LabEnvironment
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int SeedVersion { get; set; }
    public int DeterministicSeed { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? LastSeededAtUtc { get; set; }

    public ICollection<Site> Sites { get; set; } = new List<Site>();
    public ICollection<FccSimulatorProfile> Profiles { get; set; } = new List<FccSimulatorProfile>();
    public ICollection<Product> Products { get; set; } = new List<Product>();
    public ICollection<CallbackTarget> CallbackTargets { get; set; } = new List<CallbackTarget>();
    public ICollection<ScenarioDefinition> ScenarioDefinitions { get; set; } = new List<ScenarioDefinition>();
}
