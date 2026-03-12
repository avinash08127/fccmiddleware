using FccDesktopAgent.Core.Buffer;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Buffer;

public sealed class IntegrityCheckerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentDbContext _db;

    public IntegrityCheckerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CheckAndRecoverAsync_HealthyDb_ReturnsTrue()
    {
        var checker = new IntegrityChecker(_db, NullLogger<IntegrityChecker>.Instance);

        // In-memory SQLite has no file path, so it will hit the "no file" path
        // and return true (nothing to check). This validates the safe-exit path.
        var result = await checker.CheckAndRecoverAsync();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IntegrityCheck_PragmaReturnsOk_OnHealthyDb()
    {
        // Verify PRAGMA integrity_check works on our schema
        var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = await command.ExecuteScalarAsync();
            result?.ToString().Should().Be("ok");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
