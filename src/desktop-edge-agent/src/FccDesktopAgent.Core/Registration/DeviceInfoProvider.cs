using System.Reflection;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FccDesktopAgent.Core.Config;

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
        string provisioningToken,
        string siteCode,
        bool replacePreviousAgent = false,
        AgentConfiguration? agentConfig = null) =>
        new()
        {
            ProvisioningToken = provisioningToken,
            SiteCode = siteCode,
            DeviceSerialNumber = GetDeviceSerialNumber(),
            DeviceModel = GetDeviceModel(),
            OsVersion = GetOsVersion(),
            AgentVersion = GetAgentVersion(),
            DeviceClass = "DESKTOP",
            RoleCapability = "PRIMARY_ELIGIBLE",
            SiteHaPriority = 10,
            Capabilities = ["FCC_CONTROL", "PEER_API", "TRANSACTION_BUFFER", "TELEMETRY"],
            PeerApi = BuildPeerApiMetadata(agentConfig),
            ReplacePreviousAgent = replacePreviousAgent,
        };

    private static PeerApiRegistrationMetadata? BuildPeerApiMetadata(AgentConfiguration? agentConfig)
    {
        var port = agentConfig?.PeerApiPort is > 0 ? agentConfig.PeerApiPort : 8586;
        var host = GetLocalIpAddress();
        if (string.IsNullOrWhiteSpace(host))
            return null;

        return new PeerApiRegistrationMetadata
        {
            BaseUrl = $"http://{host}:{port}",
            AdvertisedHost = host,
            Port = port,
            TlsEnabled = false
        };
    }

    private static string? GetLocalIpAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                              && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork)
                ?.Address
                .ToString();
        }
        catch
        {
            return null;
        }
    }
}
