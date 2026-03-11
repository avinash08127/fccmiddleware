using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FccDesktopAgent.Benchmarks.Seed;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer.Entities;

namespace FccDesktopAgent.Benchmarks;

/// <summary>
/// Benchmarks for cloud upload replay throughput.
///
/// GUARDRAIL: Replay throughput on stable internet: >= 600 transactions/minute
///            while preserving chronological ordering (CreatedAt ASC).
///
/// Note: These benchmarks use an in-memory mock "cloud" to isolate
/// serialization + batching overhead from actual network latency.
/// Real network benchmarks belong in load tests.
/// </summary>
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[BenchmarkCategory("ReplayThroughput")]
public class ReplayThroughputBenchmarks
{
    private List<BufferedTransaction> _transactions = [];

    [GlobalSetup]
    public void Setup()
    {
        _transactions = TransactionSeeder.Generate(30_000);
    }

    /// <summary>
    /// Measures the overhead of serializing a batch of 50 transactions for cloud upload.
    /// Real network I/O is excluded — tests the agent-side CPU/memory budget.
    /// GUARDRAIL: must serialize >= 500 batches/min to support 600 tx/min at batch size 50.
    /// That is ~8.3 batches/sec, so each batch should serialize in &lt;= 120ms.
    /// </summary>
    [Benchmark(Description = "SerializeBatch50 [guardrail: <= 120ms per batch]")]
    public string SerializeBatch50()
    {
        var batch = _transactions.Take(50);
        return System.Text.Json.JsonSerializer.Serialize(batch);
    }

    /// <summary>
    /// Measures the overhead of ordering 30,000 records in-memory (worst case pre-sort).
    /// In practice, ORDER BY in SQLite handles this, but this verifies in-memory cost.
    /// </summary>
    [Benchmark(Description = "OrderBy_30k_CreatedAt_ASC")]
    public List<BufferedTransaction> OrderBy_30k_CreatedAt_Asc()
    {
        return [.. _transactions.OrderBy(t => t.CreatedAt)];
    }

    /// <summary>
    /// Measures throughput of marking a batch as Uploaded (EF change tracking cost simulation).
    /// </summary>
    [Benchmark(Description = "MarkBatchUploaded_50")]
    public void MarkBatchUploaded_50()
    {
        foreach (var tx in _transactions.Take(50))
        {
            tx.SyncStatus = SyncStatus.Uploaded;
            tx.LastUploadAttemptAt = DateTimeOffset.UtcNow;
            tx.UploadAttempts++;
        }
    }
}
