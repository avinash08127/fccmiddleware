using System.Data.Common;
using System.Text.Json;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using FccMiddleware.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Parquet.Serialization;

namespace FccMiddleware.Infrastructure.Workers;

/// <summary>
/// Archives old PostgreSQL partitions to object storage and cleans processed outbox rows.
/// </summary>
public sealed class ArchiveWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ArchiveWorker> _logger;
    private readonly ArchiveWorkerOptions _options;

    public ArchiveWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ArchiveWorker> logger,
        IOptions<ArchiveWorkerOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ArchiveWorker started. PollInterval={PollInterval}s, TransactionRetentionMonths={TransactionRetentionMonths}, AuditRetentionYears={AuditRetentionYears}, OutboxRetentionDays={OutboxRetentionDays}",
            _options.PollIntervalSeconds,
            _options.TransactionRetentionMonths,
            _options.AuditRetentionYears,
            _options.OutboxRetentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessCycleAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ArchiveWorker error during archive cycle");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorDelaySeconds), stoppingToken);
            }
        }

        _logger.LogInformation("ArchiveWorker stopped");
    }

    internal async Task<ArchiveCycleResult> ProcessCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var partitionManager = scope.ServiceProvider.GetRequiredService<IPostgresPartitionManager>();
        var objectStore = scope.ServiceProvider.GetRequiredService<IArchiveObjectStore>();

        var now = DateTimeOffset.UtcNow;
        var transactionsCutoff = StartOfMonth(now).AddMonths(-_options.TransactionRetentionMonths);
        var auditCutoff = StartOfMonth(now).AddYears(-_options.AuditRetentionYears);

        var transactionPartitions = await partitionManager.GetDetachablePartitionsAsync(
            db,
            "transactions",
            transactionsCutoff,
            ct);

        var auditPartitions = await partitionManager.GetDetachablePartitionsAsync(
            db,
            "audit_events",
            auditCutoff,
            ct);

        var archivedTransactionPartitions = 0;
        foreach (var partition in transactionPartitions)
        {
            await ArchiveTransactionPartitionAsync(db, partitionManager, objectStore, partition, ct);
            archivedTransactionPartitions++;
        }

        var archivedAuditPartitions = 0;
        foreach (var partition in auditPartitions)
        {
            await ArchiveAuditPartitionAsync(db, partitionManager, objectStore, partition, ct);
            archivedAuditPartitions++;
        }

        var deletedOutboxMessages = await CleanupOutboxMessagesAsync(db, ct);

        if (archivedTransactionPartitions > 0 || archivedAuditPartitions > 0 || deletedOutboxMessages > 0)
        {
            _logger.LogInformation(
                "ArchiveWorker archived {TransactionPartitions} transaction partitions, {AuditPartitions} audit partitions, deleted {OutboxMessages} processed outbox messages",
                archivedTransactionPartitions,
                archivedAuditPartitions,
                deletedOutboxMessages);
        }

        return new ArchiveCycleResult(
            archivedTransactionPartitions,
            archivedAuditPartitions,
            deletedOutboxMessages);
    }

    private async Task ArchiveTransactionPartitionAsync(
        FccMiddlewareDbContext db,
        IPostgresPartitionManager partitionManager,
        IArchiveObjectStore objectStore,
        PartitionInfo partition,
        CancellationToken ct)
    {
        await partitionManager.DetachPartitionAsync(db, "transactions", partition, ct);

        var rows = await ReadTransactionsAsync(db, partition, ct);
        await MarkTransactionsArchivedAsync(db, partition, ct);

        var month = partition.RangeStart ?? DateTimeOffset.UtcNow;
        var parquetKey = BuildArchiveKey("transactions", month, partition.PartitionName, "data.parquet");
        var manifestKey = BuildArchiveKey("transactions", month, partition.PartitionName, "manifest.json");

        await using var parquetStream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, parquetStream, cancellationToken: ct);
        var parquetUri = await objectStore.PutObjectAsync(parquetKey, "application/octet-stream", parquetStream, ct);

        var manifest = new ArchiveManifest
        {
            Table = "transactions",
            Partition = partition.PartitionName,
            PartitionStartUtc = partition.RangeStart,
            PartitionEndUtc = partition.RangeEnd,
            RowCount = rows.Count,
            Format = "parquet",
            ArchiveUri = parquetUri,
            ArchivedAtUtc = DateTimeOffset.UtcNow,
            AthenaReadme = "See docs/runbooks/cloud-archive-athena.md for the external table and partition registration flow."
        };

        await objectStore.PutTextAsync(
            manifestKey,
            "application/json",
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        if (_options.DropDetachedPartitionsAfterExport)
            await partitionManager.DropDetachedPartitionAsync(db, partition, ct);
    }

    private async Task ArchiveAuditPartitionAsync(
        FccMiddlewareDbContext db,
        IPostgresPartitionManager partitionManager,
        IArchiveObjectStore objectStore,
        PartitionInfo partition,
        CancellationToken ct)
    {
        await partitionManager.DetachPartitionAsync(db, "audit_events", partition, ct);

        var rows = await ReadAuditEventsAsync(db, partition, ct);
        var month = partition.RangeStart ?? DateTimeOffset.UtcNow;
        var parquetKey = BuildArchiveKey("audit_events", month, partition.PartitionName, "data.parquet");
        var manifestKey = BuildArchiveKey("audit_events", month, partition.PartitionName, "manifest.json");

        await using var parquetStream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, parquetStream, cancellationToken: ct);
        var parquetUri = await objectStore.PutObjectAsync(parquetKey, "application/octet-stream", parquetStream, ct);

        var manifest = new ArchiveManifest
        {
            Table = "audit_events",
            Partition = partition.PartitionName,
            PartitionStartUtc = partition.RangeStart,
            PartitionEndUtc = partition.RangeEnd,
            RowCount = rows.Count,
            Format = "parquet",
            ArchiveUri = parquetUri,
            ArchivedAtUtc = DateTimeOffset.UtcNow,
            AthenaReadme = "See docs/runbooks/cloud-archive-athena.md for the external table and partition registration flow."
        };

        await objectStore.PutTextAsync(
            manifestKey,
            "application/json",
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        if (_options.DropDetachedPartitionsAfterExport)
            await partitionManager.DropDetachedPartitionAsync(db, partition, ct);
    }

    private async Task<int> CleanupOutboxMessagesAsync(FccMiddlewareDbContext db, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.OutboxRetentionDays);

        return await db.OutboxMessages
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private static async Task<List<TransactionArchiveRow>> ReadTransactionsAsync(
        FccMiddlewareDbContext db,
        PartitionInfo partition,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            $"""
             SELECT id,
                    created_at,
                    legal_entity_id,
                    fcc_transaction_id,
                    site_code,
                    pump_number,
                    nozzle_number,
                    product_code,
                    volume_microlitres,
                    amount_minor_units,
                    unit_price_minor_per_litre,
                    currency_code,
                    started_at,
                    completed_at,
                    fiscal_receipt_number,
                    fcc_correlation_id,
                    fcc_vendor,
                    attendant_id,
                    status,
                    ingestion_source,
                    raw_payload_ref,
                    odoo_order_id,
                    synced_to_odoo_at,
                    pre_auth_id,
                    reconciliation_status,
                    is_duplicate,
                    duplicate_of_id,
                    is_stale,
                    correlation_id,
                    schema_version,
                    updated_at
             FROM {QuoteQualified(partition)}
             ORDER BY created_at, id;
             """;

        return await ReadAllAsync(command, reader => new TransactionArchiveRow
        {
            Id = reader.GetGuid(0),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(1),
            LegalEntityId = reader.GetGuid(2),
            FccTransactionId = reader.GetString(3),
            SiteCode = reader.GetString(4),
            PumpNumber = reader.GetInt32(5),
            NozzleNumber = reader.GetInt32(6),
            ProductCode = reader.GetString(7),
            VolumeMicrolitres = reader.GetInt64(8),
            AmountMinorUnits = reader.GetInt64(9),
            UnitPriceMinorPerLitre = reader.GetInt64(10),
            CurrencyCode = reader.GetString(11),
            StartedAt = reader.GetFieldValue<DateTimeOffset>(12),
            CompletedAt = reader.GetFieldValue<DateTimeOffset>(13),
            FiscalReceiptNumber = GetNullableString(reader, 14),
            FccCorrelationId = GetNullableString(reader, 15),
            FccVendor = reader.GetString(16),
            AttendantId = GetNullableString(reader, 17),
            Status = reader.GetString(18),
            IngestionSource = reader.GetString(19),
            RawPayloadRef = GetNullableString(reader, 20),
            OdooOrderId = GetNullableString(reader, 21),
            SyncedToOdooAt = GetNullableDateTimeOffset(reader, 22),
            PreAuthId = GetNullableGuid(reader, 23),
            ReconciliationStatus = GetNullableString(reader, 24),
            IsDuplicate = reader.GetBoolean(25),
            DuplicateOfId = GetNullableGuid(reader, 26),
            IsStale = reader.GetBoolean(27),
            CorrelationId = reader.GetGuid(28),
            SchemaVersion = reader.GetInt32(29),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(30)
        }, ct);
    }

    private static async Task<List<AuditEventArchiveRow>> ReadAuditEventsAsync(
        FccMiddlewareDbContext db,
        PartitionInfo partition,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            $"""
             SELECT id,
                    created_at,
                    legal_entity_id,
                    event_type,
                    correlation_id,
                    site_code,
                    source,
                    payload
             FROM {QuoteQualified(partition)}
             ORDER BY created_at, id;
             """;

        return await ReadAllAsync(command, reader => new AuditEventArchiveRow
        {
            Id = reader.GetGuid(0),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(1),
            LegalEntityId = reader.GetGuid(2),
            EventType = reader.GetString(3),
            CorrelationId = reader.GetGuid(4),
            SiteCode = GetNullableString(reader, 5),
            Source = reader.GetString(6),
            Payload = reader.GetString(7)
        }, ct);
    }

    private static async Task MarkTransactionsArchivedAsync(
        FccMiddlewareDbContext db,
        PartitionInfo partition,
        CancellationToken ct)
    {
        var sql =
            $"""
             UPDATE {QuoteQualified(partition)}
             SET status = {FormatSqlLiteral(TransactionStatus.ARCHIVED.ToString())},
                 updated_at = now()
             WHERE status <> {FormatSqlLiteral(TransactionStatus.ARCHIVED.ToString())};
             """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    private static async Task<List<T>> ReadAllAsync<T>(
        DbCommand command,
        Func<DbDataReader, T> projector,
        CancellationToken ct)
    {
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(projector(reader));

        return rows;
    }

    private static string? GetNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static Guid? GetNullableGuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static DateTimeOffset? GetNullableDateTimeOffset(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static DateTimeOffset StartOfMonth(DateTimeOffset value) =>
        new(value.Year, value.Month, 1, 0, 0, 0, TimeSpan.Zero);

    private static string BuildArchiveKey(string tableName, DateTimeOffset month, string partitionName, string fileName) =>
        $"archives/{tableName}/year={month:yyyy}/month={month:MM}/partition={partitionName}/{fileName}";

    private static string QuoteQualified(PartitionInfo partition) =>
        $"{QuoteIdentifier(partition.SchemaName)}.{QuoteIdentifier(partition.PartitionName)}";

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";

    private static string FormatSqlLiteral(string value) =>
        "'" + value.Replace("'", "''") + "'";
}

public sealed class ArchiveWorkerOptions
{
    public const string SectionName = "ArchiveWorker";

    public int PollIntervalSeconds { get; set; } = 3600;
    public int ErrorDelaySeconds { get; set; } = 60;
    public int TransactionRetentionMonths { get; set; } = 24;
    public int AuditRetentionYears { get; set; } = 7;
    public int OutboxRetentionDays { get; set; } = 7;
    public bool DropDetachedPartitionsAfterExport { get; set; } = true;
}

public sealed record ArchiveCycleResult(
    int ArchivedTransactionPartitions,
    int ArchivedAuditPartitions,
    int DeletedOutboxMessages);

public sealed class TransactionArchiveRow
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid LegalEntityId { get; set; }
    public string FccTransactionId { get; set; } = string.Empty;
    public string SiteCode { get; set; } = string.Empty;
    public int PumpNumber { get; set; }
    public int NozzleNumber { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public long VolumeMicrolitres { get; set; }
    public long AmountMinorUnits { get; set; }
    public long UnitPriceMinorPerLitre { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string? FiscalReceiptNumber { get; set; }
    public string? FccCorrelationId { get; set; }
    public string FccVendor { get; set; } = string.Empty;
    public string? AttendantId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string IngestionSource { get; set; } = string.Empty;
    public string? RawPayloadRef { get; set; }
    public string? OdooOrderId { get; set; }
    public DateTimeOffset? SyncedToOdooAt { get; set; }
    public Guid? PreAuthId { get; set; }
    public string? ReconciliationStatus { get; set; }
    public bool IsDuplicate { get; set; }
    public Guid? DuplicateOfId { get; set; }
    public bool IsStale { get; set; }
    public Guid CorrelationId { get; set; }
    public int SchemaVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AuditEventArchiveRow
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid LegalEntityId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid CorrelationId { get; set; }
    public string? SiteCode { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

public sealed class ArchiveManifest
{
    public string Table { get; set; } = string.Empty;
    public string Partition { get; set; } = string.Empty;
    public DateTimeOffset? PartitionStartUtc { get; set; }
    public DateTimeOffset? PartitionEndUtc { get; set; }
    public int RowCount { get; set; }
    public string Format { get; set; } = "parquet";
    public string ArchiveUri { get; set; } = string.Empty;
    public DateTimeOffset ArchivedAtUtc { get; set; }
    public string AthenaReadme { get; set; } = string.Empty;
}
