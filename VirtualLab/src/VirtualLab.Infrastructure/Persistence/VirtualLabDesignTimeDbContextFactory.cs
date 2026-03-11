using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace VirtualLab.Infrastructure.Persistence;

public sealed class VirtualLabDesignTimeDbContextFactory : IDesignTimeDbContextFactory<VirtualLabDbContext>
{
    public VirtualLabDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<VirtualLabDbContext> builder = new();
        builder.UseSqlite(
            "Data Source=virtual-lab.design.db",
            options => options.MigrationsAssembly(typeof(VirtualLabDbContext).Assembly.FullName));

        return new VirtualLabDbContext(builder.Options);
    }
}
