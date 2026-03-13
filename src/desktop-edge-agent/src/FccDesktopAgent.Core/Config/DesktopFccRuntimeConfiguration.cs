using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Security;

namespace FccDesktopAgent.Core.Config;

internal sealed record ResolvedFccRuntimeConfiguration(
    FccVendor Vendor,
    FccConnectionConfig ConnectionConfig);

internal static class DesktopFccRuntimeConfiguration
{
    public static IReadOnlySet<FccVendor> SupportedVendors { get; } = new HashSet<FccVendor>
    {
        FccVendor.Doms,
        FccVendor.Radix,
        FccVendor.Petronite,
        FccVendor.Advatec,
    };

    public static bool IsSupported(FccVendor vendor) => SupportedVendors.Contains(vendor);

    public static bool TryValidateSiteConfig(SiteConfig config, out string error)
    {
        error = string.Empty;

        // S-DSK-019: Reject non-HTTPS CloudBaseUrl in site config
        if (!string.IsNullOrWhiteSpace(config.Sync?.CloudBaseUrl)
            && !CloudUrlGuard.IsSecure(config.Sync.CloudBaseUrl))
        {
            error = $"Sync.CloudBaseUrl must use HTTPS: '{config.Sync.CloudBaseUrl}'";
            return false;
        }

        if (config.Fcc is null || !config.Fcc.Enabled)
            return true;

        if (!TryParseVendor(config.Fcc.Vendor, out var vendor))
        {
            error = "Enabled FCC config must specify a valid vendor.";
            return false;
        }

        if (!IsSupported(vendor))
        {
            error = $"FCC vendor '{vendor}' is not supported on desktop.";
            return false;
        }

        // Advatec uses localhost:5560 by default — hostAddress/port are optional.
        if (vendor != FccVendor.Advatec
            && (string.IsNullOrWhiteSpace(config.Fcc.HostAddress) || config.Fcc.Port is null or <= 0))
        {
            error = "Enabled FCC config must specify hostAddress and port.";
            return false;
        }

        if (vendor == FccVendor.Doms && IsTcp(config.Fcc.ConnectionProtocol))
        {
            if (string.IsNullOrWhiteSpace(config.Identity?.SiteCode)
                || string.IsNullOrWhiteSpace(config.Identity?.LegalEntityId)
                || string.IsNullOrWhiteSpace(config.Site?.Currency)
                || string.IsNullOrWhiteSpace(config.Site?.Timezone))
            {
                error = "DOMS TCP requires siteCode, legalEntityId, currency, and timezone in site config.";
                return false;
            }
        }

        if (vendor == FccVendor.Radix)
        {
            if (string.IsNullOrWhiteSpace(config.Fcc.SharedSecret))
            {
                error = "Radix requires SharedSecret for SHA-1 message signing.";
                return false;
            }

            if (config.Fcc.UsnCode is null or <= 0)
            {
                error = "Radix requires UsnCode (Unique Station Number, 1–999999).";
                return false;
            }

            if (config.Fcc.AuthPort is null or <= 0)
            {
                error = "Radix requires AuthPort (external authorization port).";
                return false;
            }
        }

        return true;
    }

    public static ResolvedFccRuntimeConfiguration Resolve(
        AgentConfiguration agentConfig,
        SiteConfig? siteConfig,
        TimeSpan requestTimeout,
        string? expectedSiteCode = null,
        LocalOverrideManager? overrideManager = null)
    {
        if (siteConfig?.Fcc?.Enabled == true
            && TryParseVendor(siteConfig.Fcc.Vendor, out var configuredVendor)
            && !IsSupported(configuredVendor))
        {
            throw new NotSupportedException($"FCC vendor '{configuredVendor}' is not supported on desktop.");
        }

        if (siteConfig is not null && !TryValidateSiteConfig(siteConfig, out var validationError))
            throw new InvalidOperationException(validationError);

        var fccSection = siteConfig?.Fcc;
        var vendor = ResolveVendor(agentConfig, fccSection);
        if (!IsSupported(vendor))
            throw new NotSupportedException($"FCC vendor '{vendor}' is not supported on desktop.");

        var siteCode = expectedSiteCode ?? siteConfig?.Identity?.SiteCode ?? agentConfig.SiteId;
        if (string.IsNullOrWhiteSpace(siteCode))
            throw new InvalidOperationException("FCC siteCode is not configured.");

        var baseUrl = ResolveBaseUrl(vendor, agentConfig, fccSection, overrideManager);
        var productCodeMapping = siteConfig?.Mappings?.Nozzles
            ?.Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
            .GroupBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Key, StringComparer.OrdinalIgnoreCase);

        // Petronite webhook listener port: cloud config > local config > default (8090).
        int? webhookListenerPort = fccSection?.WebhookListenerPort
            ?? (agentConfig.PetroniteWebhookListenerPort > 0 ? agentConfig.PetroniteWebhookListenerPort : null);

        // Advatec: resolve device address/port and webhook fields from cloud config.
        var advatecDeviceAddress = fccSection?.HostAddress ?? "127.0.0.1";
        var advatecDevicePort = fccSection?.AdvatecDevicePort ?? fccSection?.Port ?? 5560;
        var advatecWebhookListenerPort = fccSection?.AdvatecWebhookListenerPort;
        var advatecWebhookToken = fccSection?.AdvatecWebhookToken;
        var advatecEfdSerialNumber = fccSection?.AdvatecEfdSerialNumber;
        var advatecCustIdType = fccSection?.AdvatecCustIdType;

        // Apply JPL port override if set
        var resolvedJplPort = overrideManager?.GetEffectiveJplPort(fccSection?.Port) ?? fccSection?.Port;

        var connectionConfig = new FccConnectionConfig(
            BaseUrl: baseUrl,
            ApiKey: agentConfig.FccApiKey,
            RequestTimeout: requestTimeout,
            SiteCode: siteCode,
            ConnectionProtocol: fccSection?.ConnectionProtocol,
            JplPort: resolvedJplPort,
            AuthPort: fccSection?.AuthPort ?? fccSection?.Port,
            SharedSecret: fccSection?.SharedSecret,
            UsnCode: fccSection?.UsnCode,
            FccPumpAddressMap: fccSection?.FccPumpAddressMap,
            HeartbeatIntervalSeconds: fccSection?.HeartbeatIntervalSeconds,
            LegalEntityId: siteConfig?.Identity?.LegalEntityId,
            CurrencyCode: siteConfig?.Site?.Currency,
            Timezone: siteConfig?.Site?.Timezone,
            PumpNumberOffset: siteConfig?.Mappings?.PumpNumberOffset ?? 0,
            ProductCodeMapping: productCodeMapping,
            WebhookListenerPort: webhookListenerPort,
            AdvatecDeviceAddress: advatecDeviceAddress,
            AdvatecDevicePort: advatecDevicePort,
            AdvatecWebhookListenerPort: advatecWebhookListenerPort,
            AdvatecWebhookToken: advatecWebhookToken,
            AdvatecEfdSerialNumber: advatecEfdSerialNumber,
            AdvatecCustIdType: advatecCustIdType,
            PreAuthTimeoutSeconds: fccSection?.PreAuthTimeoutSeconds,
            FiscalReceiptTimeoutSeconds: fccSection?.FiscalReceiptTimeoutSeconds,
            ApiRequestTimeoutSeconds: fccSection?.ApiRequestTimeoutSeconds);

        return new ResolvedFccRuntimeConfiguration(vendor, connectionConfig);
    }

    internal static bool TryParseVendor(string? rawVendor, out FccVendor vendor) =>
        Enum.TryParse(rawVendor, ignoreCase: true, out vendor);

    internal static bool IsTcp(string? connectionProtocol) =>
        string.Equals(connectionProtocol, "TCP", StringComparison.OrdinalIgnoreCase);

    private static FccVendor ResolveVendor(AgentConfiguration agentConfig, SiteConfigFcc? fccSection)
    {
        if (TryParseVendor(fccSection?.Vendor, out var vendor))
            return vendor;

        return agentConfig.FccVendor;
    }

    private static string ResolveBaseUrl(FccVendor vendor, AgentConfiguration agentConfig, SiteConfigFcc? fccSection, LocalOverrideManager? overrideManager = null)
    {
        // Apply local overrides first — they take precedence over cloud config
        var host = overrideManager?.GetEffectiveFccHost(fccSection?.HostAddress ?? "") ?? fccSection?.HostAddress;
        var port = overrideManager?.GetEffectiveFccPort(fccSection?.Port ?? 0) ?? fccSection?.Port;

        if (!string.IsNullOrWhiteSpace(host) && port is > 0)
            return $"http://{host}:{port}";

        if (!string.IsNullOrWhiteSpace(agentConfig.FccBaseUrl))
            return agentConfig.GetEffectiveFccBaseUrl(overrideManager);

        // Advatec runs on localhost:5560 by default — base URL is not required from config.
        if (vendor == FccVendor.Advatec)
            return $"http://127.0.0.1:{fccSection?.AdvatecDevicePort ?? 5560}";

        throw new InvalidOperationException("FCC base URL is not configured.");
    }
}
