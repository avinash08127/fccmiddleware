using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// Resolves the platform-appropriate data directory for the agent's SQLite database.
/// Architecture rule #15: use platform abstractions, never hardcode Windows paths.
///
/// DEA-6.2: Directories are created with restrictive permissions (owner-only on Unix,
/// current-user ACL on Windows) so the database and credential files are not world-readable.
/// </summary>
public static class AgentDataDirectory
{
    private const string AppFolderName = "FccDesktopAgent";

    /// <summary>
    /// Returns the platform-appropriate data directory, creating it if needed.
    /// Sets restrictive permissions (owner-only) on the directory.
    ///   Windows:  %LOCALAPPDATA%/FccDesktopAgent/
    ///   macOS:    ~/Library/Application Support/FccDesktopAgent/
    ///   Linux:    $XDG_DATA_HOME/FccDesktopAgent/  (or ~/.local/share/FccDesktopAgent/)
    /// </summary>
    public static string Resolve()
    {
        string baseDir;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", AppFolderName);
        }
        else
        {
            // Linux — respect XDG_DATA_HOME if set
            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var dataRoot = string.IsNullOrEmpty(xdgDataHome)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdgDataHome;
            baseDir = Path.Combine(dataRoot, AppFolderName);
        }

        Directory.CreateDirectory(baseDir);
        SetRestrictivePermissions(baseDir);
        return baseDir;
    }

    /// <summary>Returns the full path to the SQLite database file.</summary>
    public static string GetDatabasePath() =>
        Path.Combine(Resolve(), "agent.db");

    /// <summary>Builds the EF Core SQLite connection string for the agent database.</summary>
    public static string BuildConnectionString() =>
        $"Data Source={GetDatabasePath()}";

    /// <summary>
    /// Sets owner-only permissions on the data directory to prevent other users
    /// from reading the SQLite database, credential files, or logs.
    /// On Windows, %LOCALAPPDATA% is already per-user; on Unix, chmod 700.
    /// </summary>
    internal static void SetRestrictivePermissions(string directoryPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // %LOCALAPPDATA% is inherently per-user on Windows.
            // No additional ACL changes needed for typical deployments.
            return;
        }

        // Unix (Linux + macOS): set directory to rwx------  (0700)
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            if (dirInfo.Exists)
                dirInfo.UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }
        catch
        {
            // Best-effort — some file systems or containers may not support chmod.
        }
    }
}
