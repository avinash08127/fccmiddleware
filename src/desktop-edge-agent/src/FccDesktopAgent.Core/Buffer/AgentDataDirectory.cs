using System.Runtime.InteropServices;

namespace FccDesktopAgent.Core.Buffer;

/// <summary>
/// Resolves the platform-appropriate data directory for the agent's SQLite database.
/// Architecture rule #15: use platform abstractions, never hardcode Windows paths.
/// </summary>
public static class AgentDataDirectory
{
    private const string AppFolderName = "FccDesktopAgent";

    /// <summary>
    /// Returns the platform-appropriate data directory, creating it if needed.
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
        return baseDir;
    }

    /// <summary>Returns the full path to the SQLite database file.</summary>
    public static string GetDatabasePath() =>
        Path.Combine(Resolve(), "agent.db");

    /// <summary>Builds the EF Core SQLite connection string for the agent database.</summary>
    public static string BuildConnectionString() =>
        $"Data Source={GetDatabasePath()}";
}
