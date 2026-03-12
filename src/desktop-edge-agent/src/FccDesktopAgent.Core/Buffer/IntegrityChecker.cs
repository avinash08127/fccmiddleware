using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// Runs PRAGMA integrity_check on app startup. If corruption is detected,
/// backs up the DB file, deletes it, and lets EF Core recreate via migrations.
/// </summary>
public sealed class IntegrityChecker
{
    private readonly AgentDbContext _db;
    private readonly ILogger<IntegrityChecker> _logger;

    public IntegrityChecker(AgentDbContext db, ILogger<IntegrityChecker> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Checks SQLite database integrity. Returns true if healthy, false if corruption
    /// was detected and recovery was performed (database was recreated).
    /// </summary>
    public async Task<bool> CheckAndRecoverAsync(CancellationToken ct = default)
    {
        string dbPath;
        try
        {
            dbPath = GetDatabasePath();
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
            {
                // No database file yet — EF Core will create it. Nothing to check.
                _logger.LogInformation("No existing database file found; skipping integrity check");
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve database path; skipping integrity check");
            return true;
        }

        try
        {
            var result = await RunIntegrityCheckAsync(ct);
            if (string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Database integrity check passed");
                return true;
            }

            _logger.LogError("Database corruption detected: {IntegrityResult}", result);
            await RecoverCorruptDatabaseAsync(dbPath, ct);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Integrity check failed with exception; attempting recovery");
            try
            {
                await RecoverCorruptDatabaseAsync(dbPath, ct);
            }
            catch (Exception recoveryEx)
            {
                _logger.LogCritical(recoveryEx, "Database recovery also failed");
            }
            return false;
        }
    }

    private async Task<string> RunIntegrityCheckAsync(CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = await command.ExecuteScalarAsync(ct);
            return result?.ToString() ?? "unknown";
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    private async Task RecoverCorruptDatabaseAsync(string dbPath, CancellationToken ct)
    {
        // Close any open connections
        await _db.Database.CloseConnectionAsync();

        // Backup corrupt file
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var backupPath = $"{dbPath}.corrupt.{timestamp}";
        File.Copy(dbPath, backupPath, overwrite: true);
        _logger.LogWarning("Corrupt database backed up to {BackupPath}", backupPath);

        // Delete corrupt file and WAL/SHM files
        File.Delete(dbPath);
        var walPath = dbPath + "-wal";
        var shmPath = dbPath + "-shm";
        if (File.Exists(walPath)) File.Delete(walPath);
        if (File.Exists(shmPath)) File.Delete(shmPath);

        // EF Core will recreate via EnsureCreated or migrations on next access
        await _db.Database.EnsureCreatedAsync(ct);
        _logger.LogWarning("Database recreated after corruption recovery. Agent will re-sync from FCC.");
    }

    private string GetDatabasePath()
    {
        var connectionString = _db.Database.GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
            return string.Empty;

        var builder = new SqliteConnectionStringBuilder(connectionString);
        return builder.DataSource;
    }
}
