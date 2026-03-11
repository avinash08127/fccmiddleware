using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.AgentConfig;

public sealed record CheckAgentVersionQuery : IRequest<Result<CheckAgentVersionResult>>
{
    public required string AgentVersion { get; init; }
}

public sealed record CheckAgentVersionResult
{
    public required string AgentVersion { get; init; }
    public required string MinimumVersion { get; init; }
    public required string LatestVersion { get; init; }
    public required bool Compatible { get; init; }
    public required bool UpdateRequired { get; init; }
    public required bool UpdateAvailable { get; init; }
    public string? UpdateUrl { get; init; }
    public string? ReleaseNotes { get; init; }
}
