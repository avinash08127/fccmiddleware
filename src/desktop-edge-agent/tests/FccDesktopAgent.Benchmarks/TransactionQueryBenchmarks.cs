using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FccDesktopAgent.Benchmarks.Seed;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FccDesktopAgent.Benchmarks;

/// <summary>
/// Benchmarks for EF Core + SQLite query performance against a 30,000-record backlog.
///
/// GUARDRAIL: GET /api/transactions p95 for first page (limit &lt;= 50) with 30,000 records: &lt;= 100ms
/// </summary>
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[BenchmarkCategory("TransactionQuery")]
public class TransactionQueryBenchmarks : IDisposable
{
    private SqliteConnection? _connection;
    private AgentDbContext? _db;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        // Use in-process SQLite with WAL mode for realistic benchmark conditions
        _connection = new SqliteConnection("Data Source=bench_transactions.db");
        await _connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        // Enable WAL mode (architecture rule #3)
        await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        if (await _db.Transactions.AnyAsync())
        {
            return; // Already seeded from a prior run
        }

        var records = TransactionSeeder.Generate(30_000);
        await _db.Transactions.AddRangeAsync(records);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Simulates GET /api/transactions?limit=50 — first page, no cursor.
    /// GUARDRAIL: p95 &lt;= 100ms
    /// </summary>
    [Benchmark(Description = "FirstPage_Limit50 [guardrail: p95 <= 100ms]")]
    public async Task<List<Core.Buffer.Entities.BufferedTransaction>> FirstPage_Limit50()
    {
        return await _db!.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .Take(50)
            .ToListAsync();
    }

    /// <summary>
    /// Simulates cursor-based pagination (afterId pattern).
    /// GUARDRAIL: p95 &lt;= 100ms
    /// </summary>
    [Benchmark(Description = "CursorPage_Limit50 [guardrail: p95 <= 100ms]")]
    public async Task<List<Core.Buffer.Entities.BufferedTransaction>> CursorPage_Limit50()
    {
        // Simulate mid-dataset cursor (record 15,000 of 30,000)
        var cursor = DateTimeOffset.UtcNow.AddDays(-7).AddSeconds(15_000 * (7 * 86400.0 / 30_000));
        return await _db!.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Pending && t.CreatedAt > cursor)
            .OrderBy(t => t.CreatedAt)
            .Take(50)
            .ToListAsync();
    }

    /// <summary>
    /// Simulates the upload replay scan — finding the oldest N Pending records.
    /// GUARDRAIL: replay throughput >= 600 tx/min
    /// </summary>
    [Benchmark(Description = "ReplayScan_Batch50 [guardrail: >= 600 tx/min overall]")]
    public async Task<List<Core.Buffer.Entities.BufferedTransaction>> ReplayScan_Batch50()
    {
        return await _db!.Transactions
            .Where(t => t.SyncStatus == SyncStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .Take(50)
            .ToListAsync();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db?.Dispose();
        _connection?.Dispose();
        try { File.Delete("bench_transactions.db"); } catch { /* ignore */ }
        try { File.Delete("bench_transactions.db-wal"); } catch { /* ignore */ }
        try { File.Delete("bench_transactions.db-shm"); } catch { /* ignore */ }
    }

    public void Dispose() => Cleanup();
}
