using System.ComponentModel.DataAnnotations;

namespace FccMiddleware.Contracts.Registration;

public sealed class DeviceRegistrationApiRequest
{
    [Required]
    [StringLength(512)]
    public string? ProvisioningToken { get; set; }

    [Required]
    [StringLength(50)]
    public string SiteCode { get; set; } = null!;

    [Required]
    [StringLength(100)]
    public string DeviceSerialNumber { get; set; } = null!;

    [Required]
    [StringLength(100)]
    public string DeviceModel { get; set; } = null!;

    [Required]
    [StringLength(50)]
    public string OsVersion { get; set; } = null!;

    [Required]
    [StringLength(50)]
    public string AgentVersion { get; set; } = null!;

    public bool ReplacePreviousAgent { get; set; }
}
