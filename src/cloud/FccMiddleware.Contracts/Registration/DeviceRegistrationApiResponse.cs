using FccMiddleware.Domain.Common;

namespace FccMiddleware.Contracts.Registration;

public sealed class DeviceRegistrationApiResponse
{
    public Guid DeviceId { get; set; }
    [Sensitive]
    public string DeviceToken { get; set; } = null!;
    [Sensitive]
    public string RefreshToken { get; set; } = null!;
    public DateTimeOffset TokenExpiresAt { get; set; }
    public string SiteCode { get; set; } = null!;
    public Guid LegalEntityId { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public object? SiteConfig { get; set; }
}
