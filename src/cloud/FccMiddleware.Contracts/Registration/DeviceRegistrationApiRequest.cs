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

    [Required]
    [StringLength(20)]
    public string DeviceClass { get; set; } = "ANDROID";

    [StringLength(40)]
    public string? RoleCapability { get; set; }

    [Range(1, 1000)]
    public int? SiteHaPriority { get; set; }

    public string[] Capabilities { get; set; } = [];

    public PeerApiRegistrationMetadata? PeerApi { get; set; }

    public bool ReplacePreviousAgent { get; set; }
}

public sealed class PeerApiRegistrationMetadata
{
    [StringLength(500)]
    public string? BaseUrl { get; set; }

    [StringLength(255)]
    public string? AdvertisedHost { get; set; }

    [Range(1, 65535)]
    public int? Port { get; set; }

    public bool TlsEnabled { get; set; }
}
