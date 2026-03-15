using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Config;

public sealed class DesktopFccRuntimeConfigurationTests
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void SiteConfig_Deserializes_NewerAdapterFields()
    {
        var rawJson = JsonSerializer.Serialize(
            TestSiteConfigFactory.Create(12) with
            {
                Identity = TestSiteConfigFactory.Create().Identity with
                {
                    DeviceId = "device-001",
                    SiteCode = "SITE-001",
                    LegalEntityId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    CurrencyCode = "TZS",
                    Timezone = "Africa/Dar_es_Salaam"
                },
                Fcc = TestSiteConfigFactory.Create().Fcc with
                {
                    ConnectionProtocol = "TCP",
                    HostAddress = "192.168.10.20",
                    Port = 8080,
                    CatchUpPullIntervalSeconds = 25,
                    HybridCatchUpIntervalSeconds = 35,
                    JplPort = 4711,
                    FcAccessCode = "fc-secret",
                    DomsCountryCode = "TZ",
                    PosVersionId = "Desktop/1.2.3",
                    ConfiguredPumps = "1,2,3",
                    DppPorts = "2001,2002",
                    ReconnectBackoffMaxSeconds = 90,
                    ClientId = "pet-client",
                    ClientSecret = "pet-secret",
                    WebhookSecret = "pet-hook",
                    OAuthTokenEndpoint = "https://pet.example/token",
                    AdvatecPumpMap = "{\"SERIAL-1\":4}",
                    PushSourceIpAllowList = ["10.0.0.10"]
                }
            },
            CamelCase);

        var parsed = JsonSerializer.Deserialize<SiteConfig>(rawJson, CamelCase);

        parsed.Should().NotBeNull();
        parsed!.Fcc.Should().NotBeNull();
        parsed.Fcc!.CatchUpPullIntervalSeconds.Should().Be(25);
        parsed.Fcc.HybridCatchUpIntervalSeconds.Should().Be(35);
        parsed.Fcc.JplPort.Should().Be(4711);
        parsed.Fcc.FcAccessCode.Should().Be("fc-secret");
        parsed.Fcc.DomsCountryCode.Should().Be("TZ");
        parsed.Fcc.PosVersionId.Should().Be("Desktop/1.2.3");
        parsed.Fcc.ConfiguredPumps.Should().Be("1,2,3");
        parsed.Fcc.DppPorts.Should().Be("2001,2002");
        parsed.Fcc.ReconnectBackoffMaxSeconds.Should().Be(90);
        parsed.Fcc.ClientId.Should().Be("pet-client");
        parsed.Fcc.ClientSecret.Should().Be("pet-secret");
        parsed.Fcc.WebhookSecret.Should().Be("pet-hook");
        parsed.Fcc.OAuthTokenEndpoint.Should().Be("https://pet.example/token");
        parsed.Fcc.AdvatecPumpMap.Should().Be("{\"SERIAL-1\":4}");
        parsed.Fcc.PushSourceIpAllowList.Should().ContainSingle().Which.Should().Be("10.0.0.10");
    }

    [Fact]
    public void Resolve_UsesProductMappingsAndCorrectJplSource()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"{nameof(DesktopFccRuntimeConfigurationTests)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var overrideManager = new LocalOverrideManager(NullLogger<LocalOverrideManager>.Instance, tempDir);
            overrideManager.SaveAll(fccHost: null, fccPort: null, jplPort: 7777, wsPort: null);

            var agentConfig = new AgentConfiguration
            {
                SiteId = "SITE-001",
                FccVendor = FccVendor.Doms,
                FccApiKey = "api-key",
            };

            var siteConfig = TestSiteConfigFactory.Create() with
            {
                Identity = TestSiteConfigFactory.Create().Identity with
                {
                    SiteCode = "SITE-001",
                    LegalEntityId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    CurrencyCode = "TZS",
                    Timezone = "Africa/Dar_es_Salaam"
                },
                Fcc = TestSiteConfigFactory.Create().Fcc with
                {
                    ConnectionProtocol = "TCP",
                    HostAddress = "192.168.10.20",
                    Port = 8080,
                    JplPort = 4711,
                    FcAccessCode = "fc-secret",
                    DomsCountryCode = "TZ",
                    PosVersionId = "Desktop/1.2.3",
                    ConfiguredPumps = "1,2,3",
                    DppPorts = "2001,2002",
                    ReconnectBackoffMaxSeconds = 90,
                    SharedSecret = "radix-secret",
                    UsnCode = 123,
                    AuthPort = 7080,
                    FccPumpAddressMap = "{\"1\":{\"pumpAddr\":\"01\",\"fp\":1}}",
                    ClientId = "pet-client",
                    ClientSecret = "pet-secret",
                    WebhookSecret = "pet-hook",
                    OAuthTokenEndpoint = "https://pet.example/token",
                    AdvatecDevicePort = 5560,
                    AdvatecWebhookToken = "adv-hook",
                    AdvatecEfdSerialNumber = "SERIAL-1",
                    AdvatecCustIdType = 2,
                    AdvatecPumpMap = "{\"SERIAL-1\":4}",
                },
                Mappings = TestSiteConfigFactory.Create().Mappings with
                {
                    PumpNumberOffset = 4,
                    Products =
                    [
                        new SiteConfigProductMapping
                        {
                            FccProductCode = "001",
                            CanonicalProductCode = "PMS",
                            DisplayName = "Petrol",
                            Active = true,
                        },
                        new SiteConfigProductMapping
                        {
                            FccProductCode = "002",
                            CanonicalProductCode = "AGO",
                            DisplayName = "Diesel",
                            Active = false,
                        },
                    ],
                },
            };

            var resolved = DesktopFccRuntimeConfiguration.Resolve(
                agentConfig,
                siteConfig,
                requestTimeout: TimeSpan.FromSeconds(15),
                overrideManager: overrideManager);

            resolved.Vendor.Should().Be(FccVendor.Doms);
            resolved.ConnectionConfig.BaseUrl.Should().Be("http://192.168.10.20:8080");
            resolved.ConnectionConfig.JplPort.Should().Be(7777);
            resolved.ConnectionConfig.FcAccessCode.Should().Be("fc-secret");
            resolved.ConnectionConfig.DomsCountryCode.Should().Be("TZ");
            resolved.ConnectionConfig.PosVersionId.Should().Be("Desktop/1.2.3");
            resolved.ConnectionConfig.ReconnectBackoffMaxSeconds.Should().Be(90);
            resolved.ConnectionConfig.ConfiguredPumps.Should().Be("1,2,3");
            resolved.ConnectionConfig.DppPorts.Should().Be("2001,2002");
            resolved.ConnectionConfig.ClientId.Should().Be("pet-client");
            resolved.ConnectionConfig.ClientSecret.Should().Be("pet-secret");
            resolved.ConnectionConfig.WebhookSecret.Should().Be("pet-hook");
            resolved.ConnectionConfig.OAuthTokenEndpoint.Should().Be("https://pet.example/token");
            resolved.ConnectionConfig.AdvatecPumpMap.Should().Be("{\"SERIAL-1\":4}");
            resolved.ConnectionConfig.ProductCodeMapping.Should().Equal(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["001"] = "PMS",
                });
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
