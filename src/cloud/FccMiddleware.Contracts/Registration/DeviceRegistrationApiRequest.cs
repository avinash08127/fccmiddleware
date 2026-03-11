namespace FccMiddleware.Contracts.Registration;

public sealed class DeviceRegistrationApiRequest
{
    public string SiteCode { get; set; } = null!;
    public string DeviceSerialNumber { get; set; } = null!;
    public string DeviceModel { get; set; } = null!;
    public string OsVersion { get; set; } = null!;
    public string AgentVersion { get; set; } = null!;
    public bool ReplacePreviousAgent { get; set; }
}
