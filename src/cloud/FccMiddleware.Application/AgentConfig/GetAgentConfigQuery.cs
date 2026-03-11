using FccMiddleware.Application.Common;
using FccMiddleware.Contracts.Config;
using MediatR;

namespace FccMiddleware.Application.AgentConfig;

/// <summary>
/// Fetches the current SiteConfig for an Edge Agent device.
/// The device is identified by its JWT claims (deviceId, siteCode, legalEntityId).
/// </summary>
public sealed record GetAgentConfigQuery : IRequest<Result<GetAgentConfigResult>>
{
    /// <summary>Device ID from JWT sub claim.</summary>
    public required Guid DeviceId { get; init; }

    /// <summary>Site code from JWT site claim.</summary>
    public required string SiteCode { get; init; }

    /// <summary>Legal entity ID from JWT lei claim.</summary>
    public required Guid LegalEntityId { get; init; }

    /// <summary>
    /// Config version from If-None-Match header.
    /// Null if client did not send the header.
    /// </summary>
    public int? ClientConfigVersion { get; init; }
}

public sealed record GetAgentConfigResult
{
    /// <summary>True when the client's config version matches the current version (304).</summary>
    public required bool NotModified { get; init; }

    /// <summary>The current config version (used for ETag header).</summary>
    public required int ConfigVersion { get; init; }

    /// <summary>Full SiteConfig payload. Null when NotModified is true.</summary>
    public SiteConfigResponse? Config { get; init; }
}
