using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using PreAuthRecord = FccDesktopAgent.Core.Buffer.Entities.PreAuthRecord;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Buffer;

public sealed class CleanupWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;

    public CleanupWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<AgentDbContext>(options => options.UseSqlite(_connection));
        services.Configure<AgentConfiguration>(cfg =>
        {
            cfg.RetentionDays = 7;
            cfg.CleanupIntervalHours = 24;
        });
        _serviceProvider = services.BuildServiceProvider();

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    private CleanupWorker CreateWorker()
    {
        return new CleanupWorker(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _serviceProvider.GetRequiredService<IOptionsMonitor<AgentConfiguration>>(),
            NullLogger<CleanupWorker>.Instance);
    }

    private AgentDbContext GetDb()
    {
        return _serviceProvider.CreateScope().ServiceProvider.GetRequiredService<AgentDbContext>();
    }

    [Fact]
    public async Task RunCleanup_DeletesOldSyncedToOdooTransactions()
    {
        using (var db = GetDb())
        {
            db.Transactions.Add(new BufferedTransaction
            {
                FccTransactionId = "OLD-SYNCED",
                SiteCode = "SITE-A",
                SyncStatus = SyncStatus.SyncedToOdoo,
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                ProductCode = "DIESEL",
                CurrencyCode = "ETB",
                FccVendor = "DOMS",
                IngestionSource = "EDGE_UPLOAD",
                RawPayloadJson = "{}"
            });
            db.Transactions.Add(new BufferedTransaction
            {
                FccTransactionId = "RECENT-SYNCED",
                SiteCode = "SITE-A",
                SyncStatus = SyncStatus.SyncedToOdoo,
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2),
                ProductCode = "DIESEL",
                CurrencyCode = "ETB",
                FccVendor = "DOMS",
                IngestionSource = "EDGE_UPLOAD",
                RawPayloadJson = "{}"
            });
            await db.SaveChangesAsync();
        }

        var worker = CreateWorker();
        await worker.RunCleanupAsync(CancellationToken.None);

        using (var db = GetDb())
        {
            var remaining = await db.Transactions.ToListAsync();
            remaining.Should().HaveCount(1);
            remaining[0].FccTransactionId.Should().Be("RECENT-SYNCED");
        }
    }

    [Fact]
    public async Task RunCleanup_DoesNotDeletePendingTransactions()
    {
        using (var db = GetDb())
        {
            db.Transactions.Add(new BufferedTransaction
            {
                FccTransactionId = "OLD-PENDING",
                SiteCode = "SITE-A",
                SyncStatus = SyncStatus.Pending,
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                ProductCode = "DIESEL",
                CurrencyCode = "ETB",
                FccVendor = "DOMS",
                IngestionSource = "EDGE_UPLOAD",
                RawPayloadJson = "{}"
            });
            await db.SaveChangesAsync();
        }

        var worker = CreateWorker();
        await worker.RunCleanupAsync(CancellationToken.None);

        using (var db = GetDb())
        {
            var count = await db.Transactions.CountAsync();
            count.Should().Be(1);
        }
    }

    [Fact]
    public async Task RunCleanup_DeletesTerminalPreAuthRecords()
    {
        using (var db = GetDb())
        {
            db.PreAuths.Add(new PreAuthRecord
            {
                SiteCode = "SITE-A",
                OdooOrderId = "ORDER-OLD",
                Status = PreAuthStatus.Completed,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                ProductCode = "DIESEL",
                Currency = "ETB",
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-10),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-9),
            });
            db.PreAuths.Add(new PreAuthRecord
            {
                SiteCode = "SITE-A",
                OdooOrderId = "ORDER-ACTIVE",
                Status = PreAuthStatus.Authorized,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                ProductCode = "DIESEL",
                Currency = "ETB",
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-10),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        }

        var worker = CreateWorker();
        await worker.RunCleanupAsync(CancellationToken.None);

        using (var db = GetDb())
        {
            var remaining = await db.PreAuths.ToListAsync();
            remaining.Should().HaveCount(1);
            remaining[0].OdooOrderId.Should().Be("ORDER-ACTIVE");
        }
    }

    [Fact]
    public async Task RunCleanup_TrimsOldAuditLogEntries()
    {
        using (var db = GetDb())
        {
            db.AuditLog.Add(new AuditLogEntry
            {
                EventType = "OLD_EVENT",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-10),
            });
            db.AuditLog.Add(new AuditLogEntry
            {
                EventType = "RECENT_EVENT",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        var worker = CreateWorker();
        await worker.RunCleanupAsync(CancellationToken.None);

        using (var db = GetDb())
        {
            var remaining = await db.AuditLog.ToListAsync();
            remaining.Should().HaveCount(1);
            remaining[0].EventType.Should().Be("RECENT_EVENT");
        }
    }

    [Fact]
    public async Task RunCleanup_DeletesOldDuplicateConfirmedTransactions()
    {
        using (var db = GetDb())
        {
            db.Transactions.Add(new BufferedTransaction
            {
                FccTransactionId = "OLD-DUP",
                SiteCode = "SITE-A",
                SyncStatus = SyncStatus.DuplicateConfirmed,
                UpdatedAt = DateTimeOffset.UtcNow.AddDays(-10),
                ProductCode = "DIESEL",
                CurrencyCode = "ETB",
                FccVendor = "DOMS",
                IngestionSource = "EDGE_UPLOAD",
                RawPayloadJson = "{}"
            });
            await db.SaveChangesAsync();
        }

        var worker = CreateWorker();
        await worker.RunCleanupAsync(CancellationToken.None);

        using (var db = GetDb())
        {
            var count = await db.Transactions.CountAsync();
            count.Should().Be(0);
        }
    }
}
