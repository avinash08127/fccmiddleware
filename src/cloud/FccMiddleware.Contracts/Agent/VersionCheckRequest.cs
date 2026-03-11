namespace FccMiddleware.Contracts.Agent;

public sealed class VersionCheckRequest
{
    public string? AppVersion { get; set; }
    public string? AgentVersion { get; set; }
}
