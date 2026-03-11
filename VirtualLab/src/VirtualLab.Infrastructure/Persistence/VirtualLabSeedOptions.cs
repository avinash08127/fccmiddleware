namespace VirtualLab.Infrastructure.Persistence;

public sealed class VirtualLabSeedOptions
{
    public const string SectionName = "VirtualLab:Seed";

    public bool ApplyOnStartup { get; set; } = true;
    public bool ResetOnStartup { get; set; }
}
