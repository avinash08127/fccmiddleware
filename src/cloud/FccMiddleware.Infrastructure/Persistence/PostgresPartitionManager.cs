using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace FccMiddleware.Infrastructure.Persistence;

public sealed record PartitionInfo(
    string SchemaName,
    string PartitionName,
    DateTimeOffset? RangeStart,
    DateTimeOffset? RangeEnd)
{
    public string QualifiedName => $"{SchemaName}.{PartitionName}";
}

public interface IPostgresPartitionManager
{
    Task<IReadOnlyList<PartitionInfo>> GetDetachablePartitionsAsync(
        FccMiddlewareDbContext db,
        string parentTable,
        DateTimeOffset cutoffExclusive,
        CancellationToken ct);

    Task DetachPartitionAsync(
        FccMiddlewareDbContext db,
        string parentTable,
        PartitionInfo partition,
        CancellationToken ct);

    Task DropDetachedPartitionAsync(
        FccMiddlewareDbContext db,
        PartitionInfo partition,
        CancellationToken ct);
}

public sealed class PostgresPartitionManager : IPostgresPartitionManager
{
    private static readonly Regex PartitionBoundRegex = new(
        @"FROM \('(?<from>[^']+)'\) TO \('(?<to>[^']+)'\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<PartitionInfo>> GetDetachablePartitionsAsync(
        FccMiddlewareDbContext db,
        string parentTable,
        DateTimeOffset cutoffExclusive,
        CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);

        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT child_ns.nspname AS schema_name,
                   child.relname AS partition_name,
                   pg_get_expr(child.relpartbound, child.oid) AS partition_bound
            FROM pg_inherits
            INNER JOIN pg_class parent ON parent.oid = pg_inherits.inhparent
            INNER JOIN pg_class child ON child.oid = pg_inherits.inhrelid
            INNER JOIN pg_namespace child_ns ON child_ns.oid = child.relnamespace
            INNER JOIN pg_namespace parent_ns ON parent_ns.oid = parent.relnamespace
            WHERE parent_ns.nspname = 'public'
              AND parent.relname = @parentTable
            ORDER BY child.relname;
            """;

        AddParameter(command, "@parentTable", parentTable);

        var partitions = new List<PartitionInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schemaName = reader.GetString(0);
            var partitionName = reader.GetString(1);
            var bound = reader.IsDBNull(2) ? null : reader.GetString(2);
            var partition = new PartitionInfo(
                schemaName,
                partitionName,
                TryParseRangeBound(bound, "from"),
                TryParseRangeBound(bound, "to"));

            if (partition.RangeEnd.HasValue && partition.RangeEnd.Value <= cutoffExclusive)
                partitions.Add(partition);
        }

        return partitions
            .OrderBy(p => p.RangeStart)
            .ThenBy(p => p.PartitionName)
            .ToList();
    }

    public async Task DetachPartitionAsync(
        FccMiddlewareDbContext db,
        string parentTable,
        PartitionInfo partition,
        CancellationToken ct)
    {
        var sql =
            $"""
             ALTER TABLE {QuoteIdentifier("public")}.{QuoteIdentifier(parentTable)}
             DETACH PARTITION {QuoteIdentifier(partition.SchemaName)}.{QuoteIdentifier(partition.PartitionName)};
             """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    public async Task DropDetachedPartitionAsync(
        FccMiddlewareDbContext db,
        PartitionInfo partition,
        CancellationToken ct)
    {
        var sql =
            $"""
             DROP TABLE IF EXISTS {QuoteIdentifier(partition.SchemaName)}.{QuoteIdentifier(partition.PartitionName)};
             """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset? TryParseRangeBound(string? partitionBound, string groupName)
    {
        if (string.IsNullOrWhiteSpace(partitionBound))
            return null;

        var match = PartitionBoundRegex.Match(partitionBound);
        if (!match.Success)
            return null;

        var value = match.Groups[groupName].Value;
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
            ? timestamp
            : null;
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
