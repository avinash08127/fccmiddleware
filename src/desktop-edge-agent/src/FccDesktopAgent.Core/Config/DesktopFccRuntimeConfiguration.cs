using FccDesktopAgent.Core.Adapter.Common;

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
    };

    public static bool IsSupported(FccVendor vendor) => SupportedVendors.Contains(vendor);

    public static bool TryValidateSiteConfig(SiteConfig config, out string error)
    {
        error = string.Empty;

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

        if (string.IsNullOrWhiteSpace(config.Fcc.HostAddress) || config.Fcc.Port is null or <= 0)
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

        return true;
    }

    public static ResolvedFccRuntimeConfiguration Resolve(
        AgentConfiguration agentConfig,
        SiteConfig? siteConfig,
        TimeSpan requestTimeout,
        string? expectedSiteCode = null)
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

        var baseUrl = ResolveBaseUrl(agentConfig, fccSection);
        var productCodeMapping = siteConfig?.Mappings?.Nozzles
            ?.Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
            .GroupBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Key, StringComparer.OrdinalIgnoreCase);

        // Petronite webhook listener port: cloud config > local config > default (8090).
        int? webhookListenerPort = fccSection?.WebhookListenerPort
            ?? (agentConfig.PetroniteWebhookListenerPort > 0 ? agentConfig.PetroniteWebhookListenerPort : null);

        var connectionConfig = new FccConnectionConfig(
            BaseUrl: baseUrl,
            ApiKey: agentConfig.FccApiKey,
            RequestTimeout: requestTimeout,
            SiteCode: siteCode,
            ConnectionProtocol: fccSection?.ConnectionProtocol,
            JplPort: fccSection?.Port,
            AuthPort: fccSection?.Port,
            HeartbeatIntervalSeconds: fccSection?.HeartbeatIntervalSeconds,
            LegalEntityId: siteConfig?.Identity?.LegalEntityId,
            CurrencyCode: siteConfig?.Site?.Currency,
            Timezone: siteConfig?.Site?.Timezone,
            PumpNumberOffset: siteConfig?.Mappings?.PumpNumberOffset ?? 0,
            ProductCodeMapping: productCodeMapping,
            WebhookListenerPort: webhookListenerPort);

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

    private static string ResolveBaseUrl(AgentConfiguration agentConfig, SiteConfigFcc? fccSection)
    {
        if (!string.IsNullOrWhiteSpace(fccSection?.HostAddress) && fccSection.Port is > 0)
            return $"http://{fccSection.HostAddress}:{fccSection.Port.Value}";

        if (!string.IsNullOrWhiteSpace(agentConfig.FccBaseUrl))
            return agentConfig.FccBaseUrl;

        throw new InvalidOperationException("FCC base URL is not configured.");
    }
}
