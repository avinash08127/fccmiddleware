using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace FccDesktopAgent.Core.Buffer.Interceptors;

/// <summary>
/// Connection interceptor that enables WAL journal mode and foreign key enforcement
/// on every new SQLite connection. Architecture rule #3: SQLite WAL mode always enabled.
/// </summary>
public sealed class SqliteWalModeInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        SetPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await SetPragmasAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void SetPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
    }

    private static async Task SetPragmasAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
