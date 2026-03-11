namespace VirtualLab.Infrastructure.Persistence.Seed;

public interface IVirtualLabSeedService
{
    Task SeedAsync(bool resetExisting, CancellationToken cancellationToken = default);
}
