using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VirtualLab.Infrastructure.Persistence;

namespace VirtualLab.Tests.Persistence;

internal sealed class SqliteTestDb : IAsyncDisposable
{
    private readonly SqliteConnection connection;

    public SqliteTestDb()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        DbContextOptionsBuilder<VirtualLabDbContext> builder = new();
        builder.UseSqlite(
            connection,
            options => options.MigrationsAssembly(typeof(VirtualLabDbContext).Assembly.FullName));

        DbContext = new VirtualLabDbContext(builder.Options);
        DbContext.Database.Migrate();
    }

    public VirtualLabDbContext DbContext { get; }

    public ValueTask DisposeAsync()
    {
        DbContext.Dispose();
        return connection.DisposeAsync();
    }
}
