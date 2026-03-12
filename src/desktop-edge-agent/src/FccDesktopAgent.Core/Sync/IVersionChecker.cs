namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Checks agent version compatibility with the cloud on startup.
/// Per requirements §15.13: agent calls /agent/version-check on startup
/// and disables FCC communication if below minimum supported version.
/// </summary>
public interface IVersionChecker
{
    /// <summary>
    /// Calls GET /api/v1/agent/version-check and returns compatibility info.
    /// Returns null if the check cannot be completed (no token, network error).
    /// </summary>
    Task<VersionCheckResult?> CheckVersionAsync(CancellationToken ct = default);
}

/// <summary>Result of a version compatibility check against the cloud.</summary>
public sealed record VersionCheckResult
{
    /// <summary>Whether the current agent version is compatible with the cloud.</summary>
    public required bool Compatible { get; init; }

    /// <summary>Minimum version required by the cloud.</summary>
    public required string MinimumVersion { get; init; }

    /// <summary>Latest available version.</summary>
    public required string LatestVersion { get; init; }

    /// <summary>Whether an update is required (agent below minimum).</summary>
    public required bool UpdateRequired { get; init; }

    /// <summary>Download URL for the update (if available).</summary>
    public string? UpdateUrl { get; init; }

    /// <summary>Whether an update is available (newer version exists).</summary>
    public required bool UpdateAvailable { get; init; }

    /// <summary>Release notes for the latest version.</summary>
    public string? ReleaseNotes { get; init; }
}
