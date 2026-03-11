using FccDesktopAgent.Core.Buffer.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// Design-time factory used by EF Core migrations tooling.
/// Instantiates <see cref="AgentDbContext"/> without the host running.
/// </summary>
internal sealed class AgentDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(
                AgentDataDirectory.BuildConnectionString(),
                b => b.MigrationsAssembly(typeof(AgentDbContext).Assembly.GetName().Name))
            .AddInterceptors(new SqliteWalModeInterceptor())
            .Options;

        return new AgentDbContext(options);
    }
}
