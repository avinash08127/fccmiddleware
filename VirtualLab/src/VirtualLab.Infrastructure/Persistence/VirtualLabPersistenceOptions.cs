namespace VirtualLab.Infrastructure.Persistence;

public sealed class VirtualLabPersistenceOptions
{
    public const string SectionName = "VirtualLab:Persistence";

    public string Provider { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=virtuallab.db";
}
