using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FccDesktopAgent.Benchmarks.Seed;
using FccDesktopAgent.Core.Buffer.Entities;

namespace FccDesktopAgent.Benchmarks;

/// <summary>
/// Benchmarks for memory footprint during typical agent operations.
///
/// GUARDRAIL: Steady-state RSS target: &lt;= 250 MB during normal operation.
///
/// These benchmarks measure allocation pressure (bytes allocated per operation)
/// using BenchmarkDotNet's [MemoryDiagnoser]. RSS is measured separately via
/// process monitoring during integration tests.
/// </summary>
[SimpleJob(RuntimeMoniker.Net90)]
[MemoryDiagnoser]
[BenchmarkCategory("Memory")]
public class MemoryFootprintBenchmarks
{
    /// <summary>
    /// Measures allocation cost of deserializing a 30,000-record seed dataset into memory.
    /// Represents the worst-case buffer materialization scenario.
    /// GUARDRAIL: Allocated bytes should not exceed 50 MB for 30,000 records.
    /// </summary>
    [Benchmark(Description = "Materialize_30k_Records [guardrail: <= 50 MB allocated]")]
    public List<BufferedTransaction> Materialize30kRecords()
    {
        return TransactionSeeder.Generate(30_000);
    }

    /// <summary>
    /// Measures allocation of a single serialization cycle for a 50-record upload batch.
    /// Steady-state operation: one batch per upload tick.
    /// </summary>
    [Benchmark(Description = "Serialize_Batch50_Allocation")]
    public string SerializeSingleBatch()
    {
        var batch = TransactionSeeder.Generate(50);
        return System.Text.Json.JsonSerializer.Serialize(batch);
    }
}
