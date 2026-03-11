using FccMiddleware.Application.Common;
using MediatR;

namespace FccMiddleware.Application.Registration;

public sealed class RegisterDeviceCommand : IRequest<Result<RegisterDeviceResult>>
{
    public required string ProvisioningToken { get; init; }
    public required string SiteCode { get; init; }
    public required string DeviceSerialNumber { get; init; }
    public required string DeviceModel { get; init; }
    public required string OsVersion { get; init; }
    public required string AgentVersion { get; init; }
    public bool ReplacePreviousAgent { get; init; }
}

public sealed class RegisterDeviceResult
{
    public required Guid DeviceId { get; init; }
    public required string DeviceToken { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset TokenExpiresAt { get; init; }
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public required DateTimeOffset RegisteredAt { get; init; }
}
