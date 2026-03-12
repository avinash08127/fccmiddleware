namespace FccMiddleware.Contracts.Agent;

public sealed class VersionCheckResponse
{
    public required bool Compatible { get; set; }
    public required string MinimumVersion { get; set; }
    public required string LatestVersion { get; set; }
    public required bool UpdateRequired { get; set; }
    public string? UpdateUrl { get; set; }
    public required string AgentVersion { get; set; }
    public required bool UpdateAvailable { get; set; }
    public string? ReleaseNotes { get; set; }
}
