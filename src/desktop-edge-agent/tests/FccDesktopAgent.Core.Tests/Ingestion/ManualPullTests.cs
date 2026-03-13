using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Ingestion;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Ingestion;

/// <summary>
/// Unit tests for DEA-2.7: Manual FCC Pull.
/// Covers no-op pull, new-transaction pull, and concurrent manual/scheduled pull serialization.
/// </summary>
public sealed class ManualPullTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AgentDbContext> _dbOptions;
    private readonly AgentDbContext _db;
    private readonly IFccAdapterFactory _adapterFactory;
    private readonly IFccAdapter _adapter;
    private readonly IServiceProvider _serviceProvider;

    public ManualPullTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentDbContext(_dbOptions);
        _db.Database.EnsureCreated();

        _adapter = Substitute.For<IFccAdapter>();
        _adapterFactory = Substitute.For<IFccAdapterFactory>();
        _adapterFactory.Create(Arg.Any<FccVendor>(), Arg.Any<FccConnectionConfig>())
            .Returns(_adapter);

        var services = new ServiceCollection();
        services.AddScoped<AgentDbContext>(_ => new AgentDbContext(_dbOptions));
        services.AddScoped<TransactionBufferManager>();
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IngestionOrchestrator CreateOrchestrator()
    {
        var monitor = Substitute.For<IOptionsMonitor<AgentConfiguration>>();
        monitor.CurrentValue.Returns(new AgentConfiguration
        {
            FccBaseUrl = "http://192.168.1.100:8080",
            FccApiKey = "test-key",
            SiteId = "SITE-A",
            FccVendor = FccVendor.Doms,
            IngestionMode = IngestionMode.Relay,
        });

        return new IngestionOrchestrator(
            _adapterFactory,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            monitor,
            NullLogger<IngestionOrchestrator>.Instance);
    }

    private static RawPayloadEnvelope MakeRaw(string json = "{}") =>
        new("DOMS", "SITE-A", json, DateTimeOffset.UtcNow);

    private static CanonicalTransaction MakeCanonical(string fccTxId) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            FccTransactionId = fccTxId,
            SiteCode = "SITE-A",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            VolumeMicrolitres = 50_000_000,
            AmountMinorUnits = 75_000,
            UnitPriceMinorPerLitre = 1500,
            CurrencyCode = "ETB",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            FccVendor = "DOMS",
            IngestionSource = "EDGE_UPLOAD",
            SchemaVersion = "1.0",
        };

    // ── No-op pull ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ManualPullAsync_NothingNew_ReturnsZeroCountsAndFetchCycles()
    {
        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([], null, HasMore: false));

        var result = await CreateOrchestrator().ManualPullAsync(pumpNumber: null, CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(0);
        result.DuplicatesSkipped.Should().Be(0);
        // One round-trip to FCC occurred (returned empty batch) — fetch cycle is counted.
        result.FetchCycles.Should().Be(1);
    }

    [Fact]
    public async Task ManualPullAsync_NoPumpNumberSpecified_FetchesAllTransactions()
    {
        var raw = MakeRaw();
        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw], "seq-1", HasMore: false));
        _adapter.NormalizeAsync(raw, Arg.Any<CancellationToken>())
            .Returns(MakeCanonical("FCC-001"));

        var result = await CreateOrchestrator().ManualPullAsync(pumpNumber: null, CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(1);
        result.FetchCycles.Should().Be(1);
        (await _db.Transactions.CountAsync()).Should().Be(1);
    }

    // ── New-transaction pull ──────────────────────────────────────────────────

    [Fact]
    public async Task ManualPullAsync_BuffersNewTransactions_AndReturnsCorrectCounts()
    {
        var raw1 = MakeRaw("{\"n\":1}");
        var raw2 = MakeRaw("{\"n\":2}");

        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw1, raw2], "seq-2", HasMore: false));
        _adapter.NormalizeAsync(raw1, Arg.Any<CancellationToken>()).Returns(MakeCanonical("FCC-A"));
        _adapter.NormalizeAsync(raw2, Arg.Any<CancellationToken>()).Returns(MakeCanonical("FCC-B"));

        var result = await CreateOrchestrator().ManualPullAsync(pumpNumber: 3, CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(2);
        result.DuplicatesSkipped.Should().Be(0);
        result.FetchCycles.Should().Be(1);
        result.LastFccSequence.Should().Be("seq-2");
        (await _db.Transactions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ManualPullAsync_WithPumpNumber_StillFetchesAllTransactions()
    {
        // pumpNumber is informational — fetch must not be restricted to a single pump
        var raw = MakeRaw();
        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw], "seq-1", HasMore: false));
        _adapter.NormalizeAsync(raw, Arg.Any<CancellationToken>())
            .Returns(MakeCanonical("FCC-PUMP2-TX"));

        var result = await CreateOrchestrator().ManualPullAsync(pumpNumber: 2, CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(1);
        await _adapter.Received(1).FetchTransactionsAsync(
            Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualPullAsync_DuplicatesAlreadyBuffered_CountedAsSkipped()
    {
        var raw = MakeRaw();
        var canonical = MakeCanonical("FCC-DUP");

        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw], "seq-1", HasMore: false));
        _adapter.NormalizeAsync(raw, Arg.Any<CancellationToken>()).Returns(canonical);

        var orchestrator = CreateOrchestrator();

        // First pull — should buffer it
        var first = await orchestrator.ManualPullAsync(null, CancellationToken.None);
        first.NewTransactionsBuffered.Should().Be(1);
        first.DuplicatesSkipped.Should().Be(0);

        // Second manual pull — same transaction returns as duplicate
        var second = await orchestrator.ManualPullAsync(null, CancellationToken.None);
        second.NewTransactionsBuffered.Should().Be(0);
        second.DuplicatesSkipped.Should().Be(1);

        (await _db.Transactions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ManualPullAsync_MultiPage_FetchesBothPages()
    {
        var raw1 = MakeRaw("{\"p\":1}");
        var raw2 = MakeRaw("{\"p\":2}");

        _adapter.FetchTransactionsAsync(
                Arg.Is<FetchCursor>(c => c.LastSequence == null),
                Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw1], "seq-p1", HasMore: true));
        _adapter.FetchTransactionsAsync(
                Arg.Is<FetchCursor>(c => c.LastSequence == "seq-p1"),
                Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw2], "seq-p2", HasMore: false));

        _adapter.NormalizeAsync(raw1, Arg.Any<CancellationToken>()).Returns(MakeCanonical("FCC-MP1"));
        _adapter.NormalizeAsync(raw2, Arg.Any<CancellationToken>()).Returns(MakeCanonical("FCC-MP2"));

        var result = await CreateOrchestrator().ManualPullAsync(null, CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(2);
        result.FetchCycles.Should().Be(2);
        result.LastFccSequence.Should().Be("seq-p2");
    }

    // ── Concurrent manual/scheduled pull serialization ────────────────────────

    [Fact]
    public async Task ManualPullAsync_ConcurrentWithScheduledPoll_SerializedNotRacing()
    {
        // Both pulls must complete without cursor corruption.
        // Sequence: scheduled poll runs while a manual pull is waiting on the lock.
        var tcs = new TaskCompletionSource<TransactionBatch>(TaskCreationOptions.RunContinuationsAsynchronously);

        int callCount = 0;
        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // First call blocks until the test releases it, simulating a slow scheduled poll.
                    return await tcs.Task;
                }
                // Second call (manual pull) returns a new transaction.
                var raw = MakeRaw("{\"call\":2}");
                _adapter.NormalizeAsync(raw, Arg.Any<CancellationToken>())
                    .Returns(MakeCanonical("FCC-MANUAL"));
                return new TransactionBatch([raw], "seq-manual", HasMore: false);
            });

        var raw0 = MakeRaw("{\"call\":1}");
        _adapter.NormalizeAsync(raw0, Arg.Any<CancellationToken>())
            .Returns(MakeCanonical("FCC-SCHED"));

        var orchestrator = CreateOrchestrator();

        // Start scheduled poll — it will block waiting for tcs.
        var scheduledTask = orchestrator.PollAndBufferAsync(CancellationToken.None);

        // Give the scheduled poll a moment to acquire the lock before releasing it.
        await Task.Delay(50);

        // Start manual pull — it will wait for the scheduled poll to finish.
        var manualTask = orchestrator.ManualPullAsync(pumpNumber: 1, CancellationToken.None);

        // Release the scheduled poll.
        tcs.SetResult(new TransactionBatch([raw0], "seq-sched", HasMore: false));

        var scheduledResult = await scheduledTask;
        var manualResult = await manualTask;

        scheduledResult.NewTransactionsBuffered.Should().Be(1);
        scheduledResult.LastFccSequence.Should().Be("seq-sched");

        manualResult.NewTransactionsBuffered.Should().Be(1);
        manualResult.LastFccSequence.Should().Be("seq-manual");

        // Both transactions must be persisted; cursor must reflect the manual pull's sequence.
        (await _db.Transactions.CountAsync()).Should().Be(2);
        var syncState = await _db.SyncStates.FindAsync(1);
        syncState!.LastFccSequence.Should().Be("seq-manual");
    }

    [Fact]
    public async Task ManualPullAsync_ConcurrentTwoManualPulls_OnlyOneFetchesAtATime()
    {
        // Two concurrent manual pulls must be serialized.
        var gate = new SemaphoreSlim(0, 1);
        int activeCount = 0;
        int maxConcurrent = 0;

        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var current = Interlocked.Increment(ref activeCount);
                maxConcurrent = Math.Max(maxConcurrent, current);
                await gate.WaitAsync(); // hold until released
                Interlocked.Decrement(ref activeCount);
                return new TransactionBatch([], null, HasMore: false);
            });

        var orchestrator = CreateOrchestrator();

        var pull1 = orchestrator.ManualPullAsync(null, CancellationToken.None);
        var pull2 = orchestrator.ManualPullAsync(null, CancellationToken.None);

        await Task.Delay(50); // let both attempts reach the semaphore

        // Release first, then second.
        gate.Release();
        await Task.Delay(20);
        gate.Release();

        await Task.WhenAll(pull1, pull2);

        // The SemaphoreSlim ensures only one FetchTransactionsAsync runs at a time.
        maxConcurrent.Should().Be(1);
    }
}
