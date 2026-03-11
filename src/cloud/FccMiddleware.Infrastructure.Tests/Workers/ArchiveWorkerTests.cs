using FccMiddleware.Domain.Interfaces;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Storage;
using FccMiddleware.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace FccMiddleware.Infrastructure.Tests.Workers;

public sealed class ArchiveWorkerTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private ServiceProvider _services = null!;
    private string _archiveRoot = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _postgres.StartAsync();

        _archiveRoot = Path.Combine(Path.GetTempPath(), "fccmiddleware-archive-tests", Guid.NewGuid().ToString("N"));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:FccMiddleware"] = _postgres.GetConnectionString(),
                ["Storage:ArchiveLocalRoot"] = _archiveRoot
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<ICurrentTenantProvider>(new TestTenantProvider());
        services.AddDbContext<FccMiddlewareDbContext>((sp, opts) =>
            opts.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("FccMiddleware")));
        services.AddSingleton<IPostgresPartitionManager, PostgresPartitionManager>();
        services.AddSingleton<IArchiveObjectStore, ArchiveObjectStore>();
        services.AddLogging();

        _services = services.BuildServiceProvider();

        await CreateSchemaAsync();
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(_archiveRoot))
            Directory.Delete(_archiveRoot, recursive: true);

        if (_services is not null)
            await _services.DisposeAsync();

        if (_postgres is not null)
            await _postgres.StopAsync();
    }

    [Fact(Skip = "Requires Docker/Testcontainers-enabled environment.")]
    public async Task ProcessCycleAsync_DetachesOldPartitions_ExportsArtifacts_AndCleansOutbox()
    {
        await SeedArchiveDataAsync();

        var worker = new ArchiveWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ArchiveWorker>.Instance,
            Options.Create(new ArchiveWorkerOptions
            {
                TransactionRetentionMonths = 24,
                AuditRetentionYears = 7,
                OutboxRetentionDays = 7,
                DropDetachedPartitionsAfterExport = false
            }));

        var result = await worker.ProcessCycleAsync(CancellationToken.None);

        result.ArchivedTransactionPartitions.Should().Be(1);
        result.ArchivedAuditPartitions.Should().Be(1);
        result.DeletedOutboxMessages.Should().Be(1);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        (await IsAttachedAsync(db, "transactions_2023_01")).Should().BeFalse();
        (await IsAttachedAsync(db, "transactions_2026_03")).Should().BeTrue();
        (await IsAttachedAsync(db, "audit_events_2018_01")).Should().BeFalse();
        (await IsAttachedAsync(db, "audit_events_2026_03")).Should().BeTrue();

        var archivedStatus = await ReadScalarAsync<string>(
            db,
            "SELECT status FROM public.transactions_2023_01 LIMIT 1;");
        archivedStatus.Should().Be("ARCHIVED");

        var recentStatus = await ReadScalarAsync<string>(
            db,
            "SELECT status FROM public.transactions_2026_03 LIMIT 1;");
        recentStatus.Should().Be("PENDING");

        var remainingOutbox = await db.OutboxMessages.CountAsync();
        remainingOutbox.Should().Be(1);

        File.Exists(Path.Combine(_archiveRoot, "archives", "transactions", "year=2023", "month=01", "partition=transactions_2023_01", "data.parquet"))
            .Should().BeTrue();
        File.Exists(Path.Combine(_archiveRoot, "archives", "transactions", "year=2023", "month=01", "partition=transactions_2023_01", "manifest.json"))
            .Should().BeTrue();
        File.Exists(Path.Combine(_archiveRoot, "archives", "audit_events", "year=2018", "month=01", "partition=audit_events_2018_01", "data.parquet"))
            .Should().BeTrue();
    }

    private async Task CreateSchemaAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var sql =
            """
            CREATE TABLE public.transactions (
                id uuid NOT NULL,
                created_at timestamptz NOT NULL,
                legal_entity_id uuid NOT NULL,
                fcc_transaction_id varchar(200) NOT NULL,
                site_code varchar(50) NOT NULL,
                pump_number int NOT NULL,
                nozzle_number int NOT NULL,
                product_code varchar(50) NOT NULL,
                volume_microlitres bigint NOT NULL,
                amount_minor_units bigint NOT NULL,
                unit_price_minor_per_litre bigint NOT NULL,
                currency_code varchar(3) NOT NULL,
                started_at timestamptz NOT NULL,
                completed_at timestamptz NOT NULL,
                fiscal_receipt_number varchar(200),
                fcc_correlation_id varchar(200),
                fcc_vendor varchar(30) NOT NULL,
                attendant_id varchar(100),
                status varchar(30) NOT NULL,
                ingestion_source varchar(30) NOT NULL,
                raw_payload_ref varchar(500),
                odoo_order_id varchar(200),
                synced_to_odoo_at timestamptz,
                pre_auth_id uuid,
                reconciliation_status varchar(30),
                is_duplicate boolean NOT NULL DEFAULT false,
                duplicate_of_id uuid,
                is_stale boolean NOT NULL DEFAULT false,
                correlation_id uuid NOT NULL,
                schema_version int NOT NULL DEFAULT 1,
                updated_at timestamptz NOT NULL
            ) PARTITION BY RANGE (created_at);

            CREATE TABLE public.transactions_2023_01 PARTITION OF public.transactions
            FOR VALUES FROM ('2023-01-01 00:00:00+00') TO ('2023-02-01 00:00:00+00');

            CREATE TABLE public.transactions_2026_03 PARTITION OF public.transactions
            FOR VALUES FROM ('2026-03-01 00:00:00+00') TO ('2026-04-01 00:00:00+00');

            CREATE TABLE public.audit_events (
                id uuid NOT NULL,
                created_at timestamptz NOT NULL,
                legal_entity_id uuid NOT NULL,
                event_type varchar(100) NOT NULL,
                correlation_id uuid NOT NULL,
                site_code varchar(50),
                source varchar(100) NOT NULL,
                payload jsonb NOT NULL
            ) PARTITION BY RANGE (created_at);

            CREATE TABLE public.audit_events_2018_01 PARTITION OF public.audit_events
            FOR VALUES FROM ('2018-01-01 00:00:00+00') TO ('2018-02-01 00:00:00+00');

            CREATE TABLE public.audit_events_2026_03 PARTITION OF public.audit_events
            FOR VALUES FROM ('2026-03-01 00:00:00+00') TO ('2026-04-01 00:00:00+00');

            CREATE TABLE public.outbox_messages (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                event_type varchar(100) NOT NULL,
                payload text NOT NULL,
                correlation_id uuid NOT NULL,
                created_at timestamptz NOT NULL,
                processed_at timestamptz NULL
            );
            """;

        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task SeedArchiveDataAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var legalEntityId = Guid.Parse("11000000-0000-0000-0000-000000000001");

        var sql =
            $$"""
             INSERT INTO public.transactions (
                 id, created_at, legal_entity_id, fcc_transaction_id, site_code, pump_number, nozzle_number,
                 product_code, volume_microlitres, amount_minor_units, unit_price_minor_per_litre, currency_code,
                 started_at, completed_at, fiscal_receipt_number, fcc_correlation_id, fcc_vendor, attendant_id,
                 status, ingestion_source, raw_payload_ref, odoo_order_id, synced_to_odoo_at, pre_auth_id,
                 reconciliation_status, is_duplicate, duplicate_of_id, is_stale, correlation_id, schema_version, updated_at)
             VALUES
                 ('10000000-0000-0000-0000-000000000001', '2023-01-15T10:00:00Z', '{{legalEntityId}}', 'TX-OLD', 'SITE-001', 1, 1,
                  'PMS', 1000000, 5000, 500, 'GHS', '2023-01-15T09:59:00Z', '2023-01-15T10:00:00Z', NULL, 'FCC-OLD', 'DOMS', NULL,
                  'PENDING', 'FCC_PUSH', NULL, NULL, NULL, NULL, NULL, false, NULL, false, '20000000-0000-0000-0000-000000000001', 1, '2023-01-15T10:00:00Z'),
                 ('10000000-0000-0000-0000-000000000002', '2026-03-10T10:00:00Z', '{{legalEntityId}}', 'TX-NEW', 'SITE-001', 1, 1,
                  'PMS', 1000000, 5000, 500, 'GHS', '2026-03-10T09:59:00Z', '2026-03-10T10:00:00Z', NULL, 'FCC-NEW', 'DOMS', NULL,
                  'PENDING', 'FCC_PUSH', NULL, NULL, NULL, NULL, NULL, false, NULL, false, '20000000-0000-0000-0000-000000000002', 1, '2026-03-10T10:00:00Z');

             INSERT INTO public.audit_events (
                 id, created_at, legal_entity_id, event_type, correlation_id, site_code, source, payload)
             VALUES
                 ('30000000-0000-0000-0000-000000000001', '2018-01-15T12:00:00Z', '{{legalEntityId}}', 'TransactionIngested', '40000000-0000-0000-0000-000000000001', 'SITE-001', 'cloud-outbox', '{"fccTransactionId":"TX-OLD"}'::jsonb),
                 ('30000000-0000-0000-0000-000000000002', '2026-03-10T12:00:00Z', '{{legalEntityId}}', 'TransactionIngested', '40000000-0000-0000-0000-000000000002', 'SITE-001', 'cloud-outbox', '{"fccTransactionId":"TX-NEW"}'::jsonb);

             INSERT INTO public.outbox_messages (event_type, payload, correlation_id, created_at, processed_at)
             VALUES
                 ('OldProcessed', '{}', '50000000-0000-0000-0000-000000000001', '2026-02-01T00:00:00Z', '2026-02-15T00:00:00Z'),
                 ('RecentProcessed', '{}', '50000000-0000-0000-0000-000000000002', '2026-03-10T00:00:00Z', '2026-03-10T12:00:00Z');
             """;

        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<bool> IsAttachedAsync(FccMiddlewareDbContext db, string partitionName)
    {
        const string sql =
            """
            SELECT COUNT(*)
            FROM pg_inherits
            INNER JOIN pg_class child ON child.oid = pg_inherits.inhrelid
            WHERE child.relname = @partitionName;
            """;

        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@partitionName";
        parameter.Value = partitionName;
        command.Parameters.Add(parameter);

        var count = (long)(await command.ExecuteScalarAsync())!;
        return count > 0;
    }

    private static async Task<T> ReadScalarAsync<T>(FccMiddlewareDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        return (T)result!;
    }

    private sealed class TestTenantProvider : ICurrentTenantProvider
    {
        public Guid? CurrentLegalEntityId => null;
    }
}
