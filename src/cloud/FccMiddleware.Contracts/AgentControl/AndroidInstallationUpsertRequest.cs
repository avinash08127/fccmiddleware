using System.ComponentModel.DataAnnotations;
using FccMiddleware.Domain.Common;

namespace FccMiddleware.Contracts.AgentControl;

/// <summary>
/// Android-only request used to register or rotate the current FCM installation token.
/// Authenticated device context supplies the owning device ID; callers do not submit it again.
/// </summary>
public sealed class AndroidInstallationUpsertRequest
{
    [Required]
    public Guid InstallationId { get; set; }

    [Required]
    [StringLength(4096, MinimumLength = 16)]
    [Sensitive]
    public string RegistrationToken { get; set; } = null!;

    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string AppVersion { get; set; } = null!;

    [Required]
    [StringLength(50, MinimumLength = 1)]
    public string OsVersion { get; set; } = null!;

    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string DeviceModel { get; set; } = null!;
}
