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
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Ingestion;

/// <summary>
/// Unit tests for <see cref="IngestionOrchestrator"/>.
/// Uses real in-memory SQLite (via shared connection) and NSubstitute mocks for the FCC adapter.
/// </summary>
public sealed class IngestionOrchestratorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AgentDbContext> _dbOptions;
    private readonly AgentDbContext _db; // used for direct assertion queries
    private readonly IFccAdapterFactory _adapterFactory;
    private readonly IFccAdapter _adapter;
    private readonly IServiceProvider _serviceProvider;

    public IngestionOrchestratorTests()
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

        // Shared connection keeps in-memory DB alive across multiple AgentDbContext instances.
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

    private IngestionOrchestrator CreateOrchestrator(AgentConfiguration? config = null)
    {
        config ??= new AgentConfiguration
        {
            FccBaseUrl = "http://192.168.1.100:8080",
            FccApiKey = "test-key",
            SiteId = "SITE-A",
            FccVendor = FccVendor.Doms,
            IngestionMode = IngestionMode.Relay,
        };

        var monitor = Substitute.For<IOptionsMonitor<AgentConfiguration>>();
        monitor.CurrentValue.Returns(config);

        return new IngestionOrchestrator(
            _adapterFactory,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            monitor,
            NullLogger<IngestionOrchestrator>.Instance);
    }

    private static RawPayloadEnvelope MakeRaw(string json = "{}") =>
        new("DOMS", "SITE-A", json, DateTimeOffset.UtcNow);

    private static CanonicalTransaction MakeCanonical(string fccTxId, string siteCode = "SITE-A") =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            FccTransactionId = fccTxId,
            SiteCode = siteCode,
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

    // ── Tests: buffering ──────────────────────────────────────────────────────

    [Fact]
    public async Task PollAndBufferAsync_BuffersNewTransactions()
    {
        var raw = MakeRaw();
        var canonical = MakeCanonical("FCC-001");

        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw], "cursor-1", HasMore: false));
        _adapter.NormalizeAsync(raw, Arg.Any<CancellationToken>())
            .Returns(canonical);

        var result = await CreateOrchestrator().PollAndBufferAsync(CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(1);
        result.DuplicatesSkipped.Should().Be(0);
        result.LastFccSequence.Should().Be("cursor-1");
        var stored = await _db.Transactions.SingleAsync();
        stored.FccTransactionId.Should().Be("FCC-001");
    }

    [Fact]
    public async Task PollAndBufferAsync_EmptyBatch_ReturnsZeroResult()
    {
        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([], null, HasMore: false));

        var result = await CreateOrchestrator().PollAndBufferAsync(CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(0);
        result.DuplicatesSkipped.Should().Be(0);
    }

    [Fact]
    public async Task PollAndBufferAsync_DuplicateTransaction_CountedInDuplicates()
    {
        var raw = MakeRaw();
        var canonical = MakeCanonical("FCC-DUP");

        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw], "cursor-1", HasMore: false));
        _adapter.NormalizeAsync(raw, Arg.Any<CancellationToken>())
            .Returns(canonical);

        var orchestrator = CreateOrchestrator();

        var first = await orchestrator.PollAndBufferAsync(CancellationToken.None);
        first.NewTransactionsBuffered.Should().Be(1);
        first.DuplicatesSkipped.Should().Be(0);

        // Same transaction returned again on the next poll.
        var second = await orchestrator.PollAndBufferAsync(CancellationToken.None);
        second.NewTransactionsBuffered.Should().Be(0);
        second.DuplicatesSkipped.Should().Be(1);

        (await _db.Transactions.CountAsync()).Should().Be(1);
    }

    // ── Tests: cursor management ──────────────────────────────────────────────

    [Fact]
    public async Task PollAndBufferAsync_AdvancesCursorInDb()
    {
        var raw = MakeRaw();
        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw], "cursor-42", HasMore: false));
        _adapter.NormalizeAsync(raw, Arg.Any<CancellationToken>())
            .Returns(MakeCanonical("FCC-002"));

        await CreateOrchestrator().PollAndBufferAsync(CancellationToken.None);

        var syncState = await _db.SyncStates.FindAsync(1);
        syncState.Should().NotBeNull();
        syncState!.LastFccSequence.Should().Be("cursor-42");
    }

    [Fact]
    public async Task PollAndBufferAsync_StartsWithExistingCursor()
    {
        _db.SyncStates.Add(new SyncStateRecord
        {
            Id = 1,
            LastFccSequence = "existing-cursor-99",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        FetchCursor? capturedCursor = null;
        _adapter.FetchTransactionsAsync(
                Arg.Do<FetchCursor>(c => capturedCursor = c),
                Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([], null, HasMore: false));

        await CreateOrchestrator().PollAndBufferAsync(CancellationToken.None);

        capturedCursor.Should().NotBeNull();
        capturedCursor!.LastSequence.Should().Be("existing-cursor-99");
    }

    [Fact]
    public async Task PollAndBufferAsync_NoCursorAdvance_DoesNotPersistSyncState()
    {
        // Batch returns no records (and null NextCursor), so cursor doesn't advance.
        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([], null, HasMore: false));

        await CreateOrchestrator().PollAndBufferAsync(CancellationToken.None);

        (await _db.SyncStates.CountAsync()).Should().Be(0);
    }

    // ── Tests: pagination ─────────────────────────────────────────────────────

    [Fact]
    public async Task PollAndBufferAsync_HasMore_FetchesAllPages()
    {
        var raw1 = MakeRaw("{\"page\":1}");
        var raw2 = MakeRaw("{\"page\":2}");

        _adapter.FetchTransactionsAsync(
                Arg.Is<FetchCursor>(c => c.LastSequence == null),
                Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw1], "cursor-p1", HasMore: true));

        _adapter.FetchTransactionsAsync(
                Arg.Is<FetchCursor>(c => c.LastSequence == "cursor-p1"),
                Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw2], "cursor-p2", HasMore: false));

        _adapter.NormalizeAsync(raw1, Arg.Any<CancellationToken>())
            .Returns(MakeCanonical("FCC-P1-TX1"));
        _adapter.NormalizeAsync(raw2, Arg.Any<CancellationToken>())
            .Returns(MakeCanonical("FCC-P2-TX1"));

        var result = await CreateOrchestrator().PollAndBufferAsync(CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(2);
        result.LastFccSequence.Should().Be("cursor-p2");
        await _adapter.Received(2).FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>());
        (await _db.Transactions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task PollAndBufferAsync_HasMoreButNullNextCursor_StopsLoop()
    {
        // Defensive: HasMore=true but NextCursor=null should not cause an infinite loop.
        var raw = MakeRaw();
        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([raw], NextCursor: null, HasMore: true));
        _adapter.NormalizeAsync(raw, Arg.Any<CancellationToken>())
            .Returns(MakeCanonical("FCC-GUARD"));

        var result = await CreateOrchestrator().PollAndBufferAsync(CancellationToken.None);

        // Should call FetchTransactionsAsync exactly once and stop.
        await _adapter.Received(1).FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>());
        result.NewTransactionsBuffered.Should().Be(1);
    }

    // ── Tests: error handling ─────────────────────────────────────────────────

    [Fact]
    public async Task PollAndBufferAsync_FetchThrows_ReturnsEmptyResult()
    {
        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("FCC unreachable"));

        var result = await CreateOrchestrator().PollAndBufferAsync(CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(0);
        result.DuplicatesSkipped.Should().Be(0);
        (await _db.Transactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task PollAndBufferAsync_NormalizeThrows_SkipsRecord_ContinuesRest()
    {
        var rawBad = MakeRaw("{\"bad\":true}");
        var rawGood = MakeRaw("{\"good\":true}");
        var canonical = MakeCanonical("FCC-GOOD");

        _adapter.FetchTransactionsAsync(Arg.Any<FetchCursor>(), Arg.Any<CancellationToken>())
            .Returns(new TransactionBatch([rawBad, rawGood], "cursor-1", HasMore: false));

        // First call throws; second returns successfully.
        _adapter.NormalizeAsync(Arg.Any<RawPayloadEnvelope>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException<CanonicalTransaction>(new InvalidOperationException("Malformed payload")),
                _ => Task.FromResult(canonical));

        var result = await CreateOrchestrator().PollAndBufferAsync(CancellationToken.None);

        result.NewTransactionsBuffered.Should().Be(1);
        (await _db.Transactions.CountAsync()).Should().Be(1);
        (await _db.Transactions.SingleAsync()).FccTransactionId.Should().Be("FCC-GOOD");
    }
}
