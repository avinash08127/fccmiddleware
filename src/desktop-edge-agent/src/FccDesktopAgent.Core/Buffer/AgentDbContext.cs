using FccDesktopAgent.Core.Buffer.Entities;
using Microsoft.EntityFrameworkCore;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// EF Core DbContext for the local SQLite agent database.
/// WAL mode and foreign key enforcement are applied via <see cref="Interceptors.SqliteWalModeInterceptor"/>
/// on every connection open. Architecture rule #3: SQLite WAL mode always enabled.
/// </summary>
public sealed class AgentDbContext : DbContext
{
    public AgentDbContext(DbContextOptions<AgentDbContext> options) : base(options) { }

    public DbSet<BufferedTransaction> Transactions => Set<BufferedTransaction>();
    public DbSet<PreAuthRecord> PreAuths => Set<PreAuthRecord>();
    public DbSet<NozzleMapping> NozzleMappings => Set<NozzleMapping>();
    public DbSet<SyncStateRecord> SyncStates => Set<SyncStateRecord>();
    public DbSet<AgentConfigRecord> AgentConfigs => Set<AgentConfigRecord>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AgentDbContext).Assembly);
    }
}
