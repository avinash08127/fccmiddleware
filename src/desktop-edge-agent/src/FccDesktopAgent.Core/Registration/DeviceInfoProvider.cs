using System.Reflection;
using System.Runtime.InteropServices;

namespace FccDesktopAgent.Core.Registration;

/// <summary>
/// Collects device fingerprint fields for the registration request.
/// </summary>
public static class DeviceInfoProvider
{
    /// <summary>Machine name or serial number for inventory tracking.</summary>
    public static string GetDeviceSerialNumber() => Environment.MachineName;

    /// <summary>Device model / runtime identifier (e.g., "win-x64", "osx-arm64").</summary>
    public static string GetDeviceModel() => RuntimeInformation.RuntimeIdentifier;

    /// <summary>OS version string (e.g., "Microsoft Windows 11 10.0.26200").</summary>
    public static string GetOsVersion() => RuntimeInformation.OSDescription;

    /// <summary>Agent assembly version (semantic version string).</summary>
    public static string GetAgentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is not null
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";
    }

    /// <summary>Builds a <see cref="DeviceRegistrationRequest"/> with device info pre-populated.</summary>
    public static DeviceRegistrationRequest BuildRequest(
        string provisioningToken, string siteCode, bool replacePreviousAgent = false) =>
        new()
        {
            ProvisioningToken = provisioningToken,
            SiteCode = siteCode,
            DeviceSerialNumber = GetDeviceSerialNumber(),
            DeviceModel = GetDeviceModel(),
            OsVersion = GetOsVersion(),
            AgentVersion = GetAgentVersion(),
            ReplacePreviousAgent = replacePreviousAgent,
        };
}
