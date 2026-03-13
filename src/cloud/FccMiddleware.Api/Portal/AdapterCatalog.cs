using System.Text.Json.Nodes;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Api.Portal;

public sealed record AdapterFieldOption(string Label, string Value);

public sealed record AdapterFieldDefinition(
    string Key,
    string Label,
    string Type,
    string Group,
    bool Required,
    bool Sensitive,
    bool Defaultable,
    bool SiteConfigurable,
    JsonNode? DefaultValue = null,
    string? Description = null,
    decimal? Min = null,
    decimal? Max = null,
    string? VisibleWhenKey = null,
    string? VisibleWhenValue = null,
    IReadOnlyList<AdapterFieldOption>? Options = null);

public sealed record AdapterCatalogEntry(
    string AdapterKey,
    string DisplayName,
    FccVendor Vendor,
    string AdapterVersion,
    IReadOnlyList<string> SupportedProtocols,
    IReadOnlyList<IngestionMethod> SupportedIngestionMethods,
    bool SupportsPreAuth,
    bool SupportsPumpStatus,
    IReadOnlyList<AdapterFieldDefinition> Fields);

public sealed class AdapterCatalogService
{
    private readonly IReadOnlyDictionary<string, AdapterCatalogEntry> _entries;

    public AdapterCatalogService()
    {
        _entries = CreateEntries()
            .ToDictionary(item => item.AdapterKey, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<AdapterCatalogEntry> GetAll() =>
        _entries.Values.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public AdapterCatalogEntry? Find(string adapterKey) =>
        _entries.TryGetValue(adapterKey, out var entry) ? entry : null;

    public AdapterCatalogEntry? Find(FccVendor vendor) =>
        _entries.Values.FirstOrDefault(item => item.Vendor == vendor);

    public AdapterSchemaDto ToSchemaDto(AdapterCatalogEntry entry) =>
        new()
        {
            AdapterKey = entry.AdapterKey,
            DisplayName = entry.DisplayName,
            Vendor = entry.Vendor.ToString(),
            AdapterVersion = entry.AdapterVersion,
            SupportedProtocols = entry.SupportedProtocols,
            SupportedIngestionMethods = entry.SupportedIngestionMethods.Select(item => item.ToString()).ToList(),
            SupportsPreAuth = entry.SupportsPreAuth,
            SupportsPumpStatus = entry.SupportsPumpStatus,
            Fields = entry.Fields.Select(ToFieldDto).ToList()
        };

    public JsonObject BuildDefaultValues(AdapterCatalogEntry entry)
    {
        var result = new JsonObject();
        foreach (var field in entry.Fields.Where(item => item.Defaultable && item.DefaultValue is not null))
        {
            result[field.Key] = field.DefaultValue!.DeepClone();
        }

        return result;
    }

    private static AdapterFieldDefinition CommonBoolean(
        string key,
        string label,
        string group,
        bool defaultable,
        bool siteConfigurable,
        bool defaultValue,
        string? description = null) =>
        new(
            key,
            label,
            "boolean",
            group,
            Required: false,
            Sensitive: false,
            Defaultable: defaultable,
            SiteConfigurable: siteConfigurable,
            DefaultValue: JsonValue.Create(defaultValue),
            Description: description);

    private static AdapterFieldDefinition CommonText(
        string key,
        string label,
        string group,
        bool defaultable,
        bool siteConfigurable,
        string? defaultValue = null,
        string? description = null,
        bool sensitive = false,
        bool required = false,
        string? visibleWhenKey = null,
        string? visibleWhenValue = null) =>
        new(
            key,
            label,
            sensitive ? "secret" : "text",
            group,
            Required: required,
            Sensitive: sensitive,
            Defaultable: defaultable,
            SiteConfigurable: siteConfigurable,
            DefaultValue: defaultValue is null ? null : JsonValue.Create(defaultValue),
            Description: description,
            VisibleWhenKey: visibleWhenKey,
            VisibleWhenValue: visibleWhenValue);

    private static AdapterFieldDefinition CommonNumber(
        string key,
        string label,
        string group,
        bool defaultable,
        bool siteConfigurable,
        int? defaultValue = null,
        int? min = null,
        int? max = null,
        string? description = null,
        bool required = false,
        string? visibleWhenKey = null,
        string? visibleWhenValue = null) =>
        new(
            key,
            label,
            "number",
            group,
            Required: required,
            Sensitive: false,
            Defaultable: defaultable,
            SiteConfigurable: siteConfigurable,
            DefaultValue: defaultValue is null ? null : JsonValue.Create(defaultValue.Value),
            Description: description,
            Min: min,
            Max: max,
            VisibleWhenKey: visibleWhenKey,
            VisibleWhenValue: visibleWhenValue);

    private static AdapterFieldDefinition CommonJson(
        string key,
        string label,
        string group,
        bool defaultable,
        bool siteConfigurable,
        JsonObject? defaultValue = null,
        string? description = null) =>
        new(
            key,
            label,
            "json",
            group,
            Required: false,
            Sensitive: false,
            Defaultable: defaultable,
            SiteConfigurable: siteConfigurable,
            DefaultValue: defaultValue,
            Description: description);

    private static AdapterFieldDefinition CommonSelect(
        string key,
        string label,
        string group,
        bool defaultable,
        bool siteConfigurable,
        string defaultValue,
        IReadOnlyList<AdapterFieldOption> options,
        string? description = null,
        bool required = false,
        string? visibleWhenKey = null,
        string? visibleWhenValue = null) =>
        new(
            key,
            label,
            "select",
            group,
            Required: required,
            Sensitive: false,
            Defaultable: defaultable,
            SiteConfigurable: siteConfigurable,
            DefaultValue: JsonValue.Create(defaultValue),
            Description: description,
            VisibleWhenKey: visibleWhenKey,
            VisibleWhenValue: visibleWhenValue,
            Options: options);

    private static AdapterFieldDefinitionDto ToFieldDto(AdapterFieldDefinition field) =>
        new()
        {
            Key = field.Key,
            Label = field.Label,
            Type = field.Type,
            Group = field.Group,
            Required = field.Required,
            Sensitive = field.Sensitive,
            Defaultable = field.Defaultable,
            SiteConfigurable = field.SiteConfigurable,
            Description = field.Description,
            Min = field.Min,
            Max = field.Max,
            VisibleWhenKey = field.VisibleWhenKey,
            VisibleWhenValue = field.VisibleWhenValue,
            Options = field.Options?.Select(item => new AdapterFieldOptionDto
            {
                Label = item.Label,
                Value = item.Value
            }).ToList()
        };

    private static IReadOnlyList<AdapterCatalogEntry> CreateEntries()
    {
        var protocols = new[]
        {
            new AdapterFieldOption("REST", ConnectionProtocol.REST.ToString()),
            new AdapterFieldOption("TCP", ConnectionProtocol.TCP.ToString()),
            new AdapterFieldOption("SOAP", ConnectionProtocol.SOAP.ToString()),
        };

        var ingestionMethods = new[]
        {
            new AdapterFieldOption("PUSH", IngestionMethod.PUSH.ToString()),
            new AdapterFieldOption("PULL", IngestionMethod.PULL.ToString()),
            new AdapterFieldOption("HYBRID", IngestionMethod.HYBRID.ToString()),
        };

        var ingestionModes = new[]
        {
            new AdapterFieldOption("Cloud Direct", IngestionMode.CLOUD_DIRECT.ToString()),
            new AdapterFieldOption("Relay", IngestionMode.RELAY.ToString()),
            new AdapterFieldOption("Buffer Always", IngestionMode.BUFFER_ALWAYS.ToString()),
        };

        var common = new List<AdapterFieldDefinition>
        {
            CommonSelect("connectionProtocol", "Connection Protocol", "Common", true, true, ConnectionProtocol.REST.ToString(), protocols, required: true),
            CommonSelect("transactionMode", "Transaction Mode", "Common", true, true, IngestionMethod.PUSH.ToString(), ingestionMethods),
            CommonSelect("ingestionMode", "Ingestion Mode", "Common", true, true, IngestionMode.CLOUD_DIRECT.ToString(), ingestionModes),
            CommonNumber("pullIntervalSeconds", "Pull Interval (seconds)", "Common", true, true, 30, 5, 3600),
            CommonNumber("catchUpPullIntervalSeconds", "Catch-Up Pull Interval (seconds)", "Common", true, true, 30, 5, 3600),
            CommonNumber("hybridCatchUpIntervalSeconds", "Hybrid Catch-Up Interval (seconds)", "Common", true, true, 30, 5, 3600),
            CommonNumber("heartbeatIntervalSeconds", "Heartbeat Interval (seconds)", "Common", true, true, 60, 5, 3600, required: true),
            CommonNumber("heartbeatTimeoutSeconds", "Heartbeat Timeout (seconds)", "Common", true, true, 180, 5, 3600, required: true),
            CommonText("hostAddress", "Host Address", "Connectivity", false, true, description: "Site-specific FCC host or IP.", required: true),
            CommonNumber("port", "Port", "Connectivity", false, true, min: 1, max: 65535, required: true),
            CommonBoolean("enabled", "Adapter Enabled", "Connectivity", false, true, true),
            CommonNumber("pumpNumberOffset", "Pump Number Offset", "Mappings", true, true, 0, -1000, 1000),
            CommonJson("productCodeMapping", "Product Code Mapping", "Mappings", true, true, new JsonObject(), "Map FCC-native product codes to canonical product codes."),
        };

        var doms = new List<AdapterFieldDefinition>(common)
        {
            CommonNumber("jplPort", "JPL Port", "DOMS", false, true, min: 1, max: 65535, visibleWhenKey: "connectionProtocol", visibleWhenValue: ConnectionProtocol.TCP.ToString()),
            CommonText("fcAccessCode", "Access Code", "DOMS", false, true, sensitive: true, visibleWhenKey: "connectionProtocol", visibleWhenValue: ConnectionProtocol.TCP.ToString()),
            CommonText("domsCountryCode", "Country Code", "DOMS", true, true, defaultValue: "ZA", visibleWhenKey: "connectionProtocol", visibleWhenValue: ConnectionProtocol.TCP.ToString()),
            CommonText("posVersionId", "POS Version ID", "DOMS", true, true, defaultValue: "FccMiddleware/1.0", visibleWhenKey: "connectionProtocol", visibleWhenValue: ConnectionProtocol.TCP.ToString()),
            CommonNumber("reconnectBackoffMaxSeconds", "Reconnect Backoff Max (seconds)", "DOMS", true, true, 60, 5, 600, visibleWhenKey: "connectionProtocol", visibleWhenValue: ConnectionProtocol.TCP.ToString()),
            CommonText("configuredPumps", "Configured Pumps", "DOMS", false, true, visibleWhenKey: "connectionProtocol", visibleWhenValue: ConnectionProtocol.TCP.ToString()),
            CommonText("dppPorts", "DPP Ports", "DOMS", false, true, visibleWhenKey: "connectionProtocol", visibleWhenValue: ConnectionProtocol.TCP.ToString()),
        };

        var radix = new List<AdapterFieldDefinition>(common)
        {
            CommonText("sharedSecret", "Shared Secret", "Radix", false, true, sensitive: true),
            CommonNumber("usnCode", "USN Code", "Radix", false, true, min: 1, max: 999999),
            CommonNumber("authPort", "Auth Port", "Radix", false, true, min: 1, max: 65535),
            CommonJson("fccPumpAddressMap", "Pump Address Map", "Radix", false, true, new JsonObject()),
        };

        var petronite = new List<AdapterFieldDefinition>(common)
        {
            CommonText("clientId", "Client ID", "Petronite", false, true),
            CommonText("clientSecret", "Client Secret", "Petronite", false, true, sensitive: true),
            CommonText("webhookSecret", "Webhook Secret", "Petronite", false, true, sensitive: true),
            CommonText("oauthTokenEndpoint", "OAuth Token Endpoint", "Petronite", true, true, defaultValue: "https://api.petronite.com/oauth/token"),
        };

        var advatec = new List<AdapterFieldDefinition>(common)
        {
            CommonNumber("advatecDevicePort", "Device Port", "Advatec", true, true, 5560, 1, 65535),
            CommonText("advatecWebhookToken", "Webhook Token", "Advatec", false, true, sensitive: true),
            CommonText("advatecEfdSerialNumber", "EFD Serial Number", "Advatec", false, true),
            CommonNumber("advatecCustIdType", "Default CustIdType", "Advatec", true, true, 1, 1, 6),
            CommonJson("advatecPumpMap", "Pump Map", "Advatec", false, true, new JsonObject()),
        };

        return
        [
            new AdapterCatalogEntry(
                "DOMS",
                "DOMS",
                FccVendor.DOMS,
                "1.0.0",
                [ConnectionProtocol.REST.ToString(), ConnectionProtocol.TCP.ToString()],
                [IngestionMethod.PUSH, IngestionMethod.PULL, IngestionMethod.HYBRID],
                SupportsPreAuth: false,
                SupportsPumpStatus: false,
                Fields: doms),
            new AdapterCatalogEntry(
                "RADIX",
                "Radix",
                FccVendor.RADIX,
                "1.0.0",
                [ConnectionProtocol.REST.ToString()],
                [IngestionMethod.PUSH, IngestionMethod.PULL],
                SupportsPreAuth: false,
                SupportsPumpStatus: false,
                Fields: radix),
            new AdapterCatalogEntry(
                "PETRONITE",
                "Petronite",
                FccVendor.PETRONITE,
                "1.0.0",
                [ConnectionProtocol.REST.ToString()],
                [IngestionMethod.PUSH],
                SupportsPreAuth: false,
                SupportsPumpStatus: false,
                Fields: petronite),
            new AdapterCatalogEntry(
                "ADVATEC",
                "Advatec",
                FccVendor.ADVATEC,
                "1.0.0",
                [ConnectionProtocol.REST.ToString()],
                [IngestionMethod.PUSH],
                SupportsPreAuth: false,
                SupportsPumpStatus: false,
                Fields: advatec),
        ];
    }
}
