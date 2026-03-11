using FccMiddleware.Application.Common;
using MediatR;
using Microsoft.Extensions.Configuration;

namespace FccMiddleware.Application.AgentConfig;

public sealed class CheckAgentVersionHandler
    : IRequestHandler<CheckAgentVersionQuery, Result<CheckAgentVersionResult>>
{
    private readonly IConfiguration _configuration;

    public CheckAgentVersionHandler(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public Task<Result<CheckAgentVersionResult>> Handle(
        CheckAgentVersionQuery request,
        CancellationToken cancellationToken)
    {
        if (!SemanticVersion.TryParse(request.AgentVersion, out var agentVersion))
        {
            return Task.FromResult(Result<CheckAgentVersionResult>.Failure(
                "INVALID_AGENT_VERSION",
                "appVersion must be a semantic version in the format x.y.z."));
        }

        var section = _configuration.GetSection("EdgeAgentDefaults:Rollout");
        var minimumVersionRaw = section["MinAgentVersion"] ?? "1.0.0";
        var latestVersionRaw = section["LatestAgentVersion"] ?? minimumVersionRaw;

        if (!SemanticVersion.TryParse(minimumVersionRaw, out var minimumVersion))
        {
            return Task.FromResult(Result<CheckAgentVersionResult>.Failure(
                "VERSION_CONFIG_INVALID",
                "Configured minimum agent version is not a valid semantic version."));
        }

        if (!SemanticVersion.TryParse(latestVersionRaw, out var latestVersion))
        {
            return Task.FromResult(Result<CheckAgentVersionResult>.Failure(
                "VERSION_CONFIG_INVALID",
                "Configured latest agent version is not a valid semantic version."));
        }

        if (latestVersion.CompareTo(minimumVersion) < 0)
        {
            return Task.FromResult(Result<CheckAgentVersionResult>.Failure(
                "VERSION_CONFIG_INVALID",
                "Configured latest agent version cannot be lower than the minimum supported version."));
        }

        var compatible = agentVersion.CompareTo(minimumVersion) >= 0;
        var updateRequired = !compatible;
        var updateAvailable = agentVersion.CompareTo(latestVersion) < 0;

        return Task.FromResult(Result<CheckAgentVersionResult>.Success(new CheckAgentVersionResult
        {
            AgentVersion = agentVersion.ToString(),
            MinimumVersion = minimumVersion.ToString(),
            LatestVersion = latestVersion.ToString(),
            Compatible = compatible,
            UpdateRequired = updateRequired,
            UpdateAvailable = updateAvailable,
            UpdateUrl = section["UpdateUrl"] ?? section["DownloadUrl"],
            ReleaseNotes = section["LatestReleaseNotes"] ?? section["ReleaseNotes"]
        }));
    }
}
