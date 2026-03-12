using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Buffer;

/// <summary>
/// In-memory SQLite tests for <see cref="TransactionBufferManager"/>.
/// Uses real SQLite to validate unique constraint dedup behavior.
/// </summary>
public sealed class TransactionBufferManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AgentDbContext _db;
    private readonly TransactionBufferManager _manager;

    public TransactionBufferManagerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentDbContext(options);
        _db.Database.EnsureCreated();

        _manager = new TransactionBufferManager(_db, NullLogger<TransactionBufferManager>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static CanonicalTransaction MakeTransaction(
        string fccTxId = "FCC-001",
        string siteCode = "SITE-A",
        int pumpNumber = 1,
        DateTimeOffset? completedAt = null)
    {
        return new CanonicalTransaction
        {
            Id = Guid.NewGuid().ToString(),
            FccTransactionId = fccTxId,
            SiteCode = siteCode,
            PumpNumber = pumpNumber,
            NozzleNumber = 1,
            ProductCode = "DIESEL",
            VolumeMicrolitres = 50_000_000,
            AmountMinorUnits = 75_000,
            UnitPriceMinorPerLitre = 1500,
            CurrencyCode = "ETB",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = completedAt ?? DateTimeOffset.UtcNow,
            FccVendor = "DOMS",
            IngestionSource = "EDGE_UPLOAD",
            SchemaVersion = "1.0",
            CorrelationId = Guid.NewGuid().ToString(),
        };
    }

    // --- BufferTransactionAsync ---

    [Fact]
    public async Task BufferTransactionAsync_InsertsNewTransaction()
    {
        var tx = MakeTransaction();
        var result = await _manager.BufferTransactionAsync(tx);

        result.Should().BeTrue();
        var stored = await _db.Transactions.SingleAsync();
        stored.FccTransactionId.Should().Be(tx.FccTransactionId);
        stored.SiteCode.Should().Be(tx.SiteCode);
        stored.SyncStatus.Should().Be(SyncStatus.Pending);
        stored.Status.Should().Be(TransactionStatus.Pending);
        stored.AmountMinorUnits.Should().Be(75_000);
        stored.VolumeMicrolitres.Should().Be(50_000_000);
    }

    [Fact]
    public async Task BufferTransactionAsync_DuplicateDedup_ReturnsFalse()
    {
        var tx1 = MakeTransaction(fccTxId: "FCC-DEDUP", siteCode: "SITE-A");
        var tx2 = MakeTransaction(fccTxId: "FCC-DEDUP", siteCode: "SITE-A");

        var first = await _manager.BufferTransactionAsync(tx1);
        var second = await _manager.BufferTransactionAsync(tx2);

        first.Should().BeTrue();
        second.Should().BeFalse();

        var count = await _db.Transactions.CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task BufferTransactionAsync_SameFccIdDifferentSite_BothInserted()
    {
        var tx1 = MakeTransaction(fccTxId: "FCC-MULTI", siteCode: "SITE-A");
        var tx2 = MakeTransaction(fccTxId: "FCC-MULTI", siteCode: "SITE-B");

        (await _manager.BufferTransactionAsync(tx1)).Should().BeTrue();
        (await _manager.BufferTransactionAsync(tx2)).Should().BeTrue();

        var count = await _db.Transactions.CountAsync();
        count.Should().Be(2);
    }

    // --- GetPendingBatchAsync ---

    [Fact]
    public async Task GetPendingBatchAsync_ReturnsOldestFirst()
    {
        var older = MakeTransaction(fccTxId: "OLD");
        var newer = MakeTransaction(fccTxId: "NEW");

        await _manager.BufferTransactionAsync(older);
        await Task.Delay(10); // ensure different CreatedAt
        await _manager.BufferTransactionAsync(newer);

        var batch = await _manager.GetPendingBatchAsync(10);

        batch.Should().HaveCount(2);
        batch[0].FccTransactionId.Should().Be("OLD");
        batch[1].FccTransactionId.Should().Be("NEW");
    }

    [Fact]
    public async Task GetPendingBatchAsync_RespectsLimit()
    {
        for (int i = 0; i < 5; i++)
            await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: $"TX-{i}"));

        var batch = await _manager.GetPendingBatchAsync(3);
        batch.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetPendingBatchAsync_ExcludesUploaded()
    {
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "PENDING"));
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "UPLOADED"));

        var uploaded = await _db.Transactions.SingleAsync(t => t.FccTransactionId == "UPLOADED");
        uploaded.SyncStatus = SyncStatus.Uploaded;
        await _db.SaveChangesAsync();

        var batch = await _manager.GetPendingBatchAsync(10);
        batch.Should().HaveCount(1);
        batch[0].FccTransactionId.Should().Be("PENDING");
    }

    // --- MarkUploadedAsync ---

    [Fact]
    public async Task MarkUploadedAsync_UpdatesSyncStatus()
    {
        var tx = MakeTransaction(fccTxId: "TO-UPLOAD");
        await _manager.BufferTransactionAsync(tx);

        var entity = await _db.Transactions.SingleAsync();
        var updated = await _manager.MarkUploadedAsync([entity.Id]);

        updated.Should().Be(1);
        var reloaded = await _db.Transactions.AsNoTracking().SingleAsync();
        reloaded.SyncStatus.Should().Be(SyncStatus.Uploaded);
    }

    [Fact]
    public async Task MarkUploadedAsync_EmptyList_ReturnsZero()
    {
        var result = await _manager.MarkUploadedAsync([]);
        result.Should().Be(0);
    }

    // --- MarkDuplicateConfirmedAsync ---

    [Fact]
    public async Task MarkDuplicateConfirmedAsync_SetsStatusAndSyncStatus()
    {
        var tx = MakeTransaction(fccTxId: "DUP");
        await _manager.BufferTransactionAsync(tx);

        var entity = await _db.Transactions.SingleAsync();
        await _manager.MarkDuplicateConfirmedAsync([entity.Id]);

        var reloaded = await _db.Transactions.AsNoTracking().SingleAsync();
        reloaded.SyncStatus.Should().Be(SyncStatus.DuplicateConfirmed);
        reloaded.Status.Should().Be(TransactionStatus.Duplicate);
    }

    // --- MarkSyncedToOdooAsync ---

    [Fact]
    public async Task MarkSyncedToOdooAsync_MarksByFccTransactionId()
    {
        var tx = MakeTransaction(fccTxId: "SYNCED");
        await _manager.BufferTransactionAsync(tx);

        // First mark as uploaded
        var entity = await _db.Transactions.SingleAsync();
        await _manager.MarkUploadedAsync([entity.Id]);

        // Then mark synced to Odoo
        var updated = await _manager.MarkSyncedToOdooAsync(["SYNCED"]);
        updated.Should().Be(1);

        var reloaded = await _db.Transactions.AsNoTracking().SingleAsync();
        reloaded.SyncStatus.Should().Be(SyncStatus.SyncedToOdoo);
        reloaded.Status.Should().Be(TransactionStatus.SyncedToOdoo);
    }

    [Fact]
    public async Task MarkSyncedToOdooAsync_IgnoresPendingRecords()
    {
        var tx = MakeTransaction(fccTxId: "STILL-PENDING");
        await _manager.BufferTransactionAsync(tx);

        // Don't mark as uploaded first — should not transition
        var updated = await _manager.MarkSyncedToOdooAsync(["STILL-PENDING"]);
        updated.Should().Be(0);
    }

    // --- GetForLocalApiAsync ---

    [Fact]
    public async Task GetForLocalApiAsync_ExcludesSyncedToOdoo()
    {
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "VISIBLE"));
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "HIDDEN"));

        // Mark one as synced to Odoo (via Uploaded first)
        var hidden = await _db.Transactions.SingleAsync(t => t.FccTransactionId == "HIDDEN");
        hidden.SyncStatus = SyncStatus.SyncedToOdoo;
        await _db.SaveChangesAsync();

        var results = await _manager.GetForLocalApiAsync(null, 50, 0);
        results.Should().HaveCount(1);
        results[0].FccTransactionId.Should().Be("VISIBLE");
    }

    [Fact]
    public async Task GetForLocalApiAsync_FiltersByPumpNumber()
    {
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "P1", pumpNumber: 1));
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "P2", pumpNumber: 2));

        var results = await _manager.GetForLocalApiAsync(pumpNumber: 1, limit: 50, offset: 0);
        results.Should().HaveCount(1);
        results[0].FccTransactionId.Should().Be("P1");
    }

    [Fact]
    public async Task GetForLocalApiAsync_OrdersByCompletedAtDesc()
    {
        var older = MakeTransaction(fccTxId: "OLDER", completedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = MakeTransaction(fccTxId: "NEWER", completedAt: DateTimeOffset.UtcNow);

        await _manager.BufferTransactionAsync(older);
        await _manager.BufferTransactionAsync(newer);

        var results = await _manager.GetForLocalApiAsync(null, 50, 0);
        results.Should().HaveCount(2);
        results[0].FccTransactionId.Should().Be("NEWER");
        results[1].FccTransactionId.Should().Be("OLDER");
    }

    [Fact]
    public async Task GetForLocalApiAsync_RespectsLimitAndOffset()
    {
        for (int i = 0; i < 5; i++)
        {
            await _manager.BufferTransactionAsync(MakeTransaction(
                fccTxId: $"TX-{i}",
                completedAt: DateTimeOffset.UtcNow.AddMinutes(-i)));
        }

        var page = await _manager.GetForLocalApiAsync(null, limit: 2, offset: 2);
        page.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetForLocalApiAsync_ExcludesDuplicateConfirmed()
    {
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "NORMAL"));
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "DUP-CONFIRMED"));

        var dup = await _db.Transactions.SingleAsync(t => t.FccTransactionId == "DUP-CONFIRMED");
        dup.SyncStatus = SyncStatus.DuplicateConfirmed;
        await _db.SaveChangesAsync();

        var results = await _manager.GetForLocalApiAsync(null, 50, 0);
        results.Should().HaveCount(1);
        results[0].FccTransactionId.Should().Be("NORMAL");
    }

    // --- GetBufferStatsAsync ---

    [Fact]
    public async Task GetBufferStatsAsync_ReturnsCorrectCounts()
    {
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "P1"));
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "P2"));
        await _manager.BufferTransactionAsync(MakeTransaction(fccTxId: "U1"));

        var uploaded = await _db.Transactions.SingleAsync(t => t.FccTransactionId == "U1");
        uploaded.SyncStatus = SyncStatus.Uploaded;
        await _db.SaveChangesAsync();

        var stats = await _manager.GetBufferStatsAsync();

        stats.Pending.Should().Be(2);
        stats.Uploaded.Should().Be(1);
        stats.DuplicateConfirmed.Should().Be(0);
        stats.SyncedToOdoo.Should().Be(0);
        stats.Archived.Should().Be(0);
        stats.Total.Should().Be(3);
    }

    [Fact]
    public async Task GetBufferStatsAsync_EmptyDb_AllZeros()
    {
        var stats = await _manager.GetBufferStatsAsync();
        stats.Total.Should().Be(0);
    }
}
