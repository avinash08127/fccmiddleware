using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Adapter.Doms.Tests;

internal static class TestHelpers
{
    internal static SiteFccConfig DefaultConfig(
        string siteCode = "MW-001",
        int pumpNumberOffset = 0,
        Dictionary<string, string>? productMapping = null) =>
        new()
        {
            SiteCode = siteCode,
            FccVendor = FccVendor.DOMS,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress = "192.168.1.10",
            Port = 8080,
            ApiKey = "test-api-key",
            IngestionMethod = IngestionMethod.PUSH,
            CurrencyCode = "MWK",
            Timezone = "Africa/Blantyre",
            PumpNumberOffset = pumpNumberOffset,
            ProductCodeMapping = productMapping ?? new Dictionary<string, string>
            {
                { "01", "PMS" },
                { "02", "AGO" },
                { "03", "IK" }
            }
        };

    internal static DomsCloudAdapter CreateAdapter(SiteFccConfig? config = null) =>
        new(new HttpClient { BaseAddress = new Uri("http://localhost:8080/api/v1/") },
            config ?? DefaultConfig());

    internal static string ReadFixture(string fileName)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        return File.ReadAllText(Path.Combine(dir, fileName));
    }

    internal static RawPayloadEnvelope WrapPayload(string json, string siteCode = "MW-001") =>
        new()
        {
            Vendor = FccVendor.DOMS,
            SiteCode = siteCode,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            ContentType = "application/json",
            Payload = json
        };
}
