using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.Ingestion;

/// <summary>
/// Integration tests for vendor-specific push ingress endpoints:
///   POST /api/v1/ingest/radix             — raw XML from Radix FDC (CLOUD_DIRECT)
///   POST /api/v1/ingest/petronite/webhook — webhook JSON from Petronite
///   POST /api/v1/ingest/advatec/webhook   — webhook JSON from Advatec EFD (ADV-6.2)
/// </summary>
[Collection("Integration")]
public sealed class VendorPushIngressTests : IAsyncLifetime
{
    private const string RadixSiteCode = "RADIX-SITE-001";
    private const int RadixUsnCode = 12345;
    private const string RadixSharedSecret = "radix-test-shared-secret";

    private const string PetroniteSiteCode = "PETRO-SITE-001";
    private const string PetroniteWebhookSecret = "petronite-wh-secret-32chars!!";

    private const string AdvatecSiteCode = "ADVATEC-SITE-001";
    private const string AdvatecWebhookToken = "advatec-wh-token-test-32chars!!";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine").Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:FccMiddleware"] = _postgres.GetConnectionString(),
                        ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
                    });
                });
            });

        _ = _factory.Server;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        await db.Database.EnsureCreatedAsync();
        await SeedTestDataAsync(db);

        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    // ── Radix XML Ingress Tests ──────────────────────────────────────────────

    [Fact]
    public async Task RadixIngest_ValidXml_ReturnsXmlAckWithAccepted()
    {
        var xml = BuildRadixTransactionXml("FDC-001", "100001", "15.250", "12425.00", "815.00");

        var response = await PostRadixXmlAsync(xml, RadixUsnCode);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("<STATUS>OK</STATUS>");
        body.Should().Contain("<RESULT>ACCEPTED</RESULT>");
        body.Should().Contain("<TRANSACTION_ID>");

        // Verify stored in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var stored = await db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.SiteCode == RadixSiteCode && t.FccTransactionId == "FDC-001-100001");

        stored.Should().NotBeNull();
        stored!.Status.Should().Be(TransactionStatus.PENDING);
        stored.FccVendor.Should().Be(FccVendor.RADIX);
    }

    [Fact]
    public async Task RadixIngest_DuplicateXml_ReturnsDuplicate()
    {
        var xml = BuildRadixTransactionXml("FDC-002", "200001", "10.000", "9000.00", "900.00");

        // First ingest
        var first = await PostRadixXmlAsync(xml, RadixUsnCode);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadAsStringAsync();
        firstBody.Should().Contain("<RESULT>ACCEPTED</RESULT>");

        // Second ingest — same FDC_NUM + FDC_SAVE_NUM
        var second = await PostRadixXmlAsync(xml, RadixUsnCode);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadAsStringAsync();
        secondBody.Should().Contain("<RESULT>DUPLICATE</RESULT>");
    }

    [Fact]
    public async Task RadixIngest_MissingUsnCode_Returns400()
    {
        var xml = BuildRadixTransactionXml("FDC-003", "300001", "5.000", "4500.00", "900.00");

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/radix")
        {
            Content = new StringContent(xml, Encoding.UTF8, "text/xml")
        };
        // No X-Usn-Code header

        var response = await _client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("MISSING_USN_CODE");
    }

    [Fact]
    public async Task RadixIngest_UnknownUsnCode_Returns404()
    {
        var xml = BuildRadixTransactionXml("FDC-004", "400001", "5.000", "4500.00", "900.00");

        var response = await PostRadixXmlAsync(xml, usnCode: 99999);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("USN_NOT_FOUND");
    }

    [Fact]
    public async Task RadixIngest_EmptyBody_Returns400()
    {
        var response = await PostRadixXmlAsync("", RadixUsnCode);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("EMPTY_PAYLOAD");
    }

    // ── Petronite Webhook Tests ──────────────────────────────────────────────

    [Fact]
    public async Task PetroniteWebhook_ValidPayload_ReturnsAccepted()
    {
        var payload = BuildPetroniteWebhookPayload("ORDER-001", 2, 1, 25.500m, 20775.00m, 815.00m);

        var response = await PostPetroniteWebhookAsync(payload, PetroniteWebhookSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ACCEPTED");
        body.GetProperty("transactionId").GetGuid().Should().NotBeEmpty();

        // Verify stored in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var stored = await db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.SiteCode == PetroniteSiteCode);

        stored.Should().NotBeNull();
        stored!.Status.Should().Be(TransactionStatus.PENDING);
        stored.FccVendor.Should().Be(FccVendor.PETRONITE);
    }

    [Fact]
    public async Task PetroniteWebhook_DuplicatePayload_ReturnsDuplicate()
    {
        var payload = BuildPetroniteWebhookPayload("ORDER-DEDUP-001", 3, 1, 10.000m, 9000.00m, 900.00m);

        var first = await PostPetroniteWebhookAsync(payload, PetroniteWebhookSecret);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await PostPetroniteWebhookAsync(payload, PetroniteWebhookSecret);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("DUPLICATE");
    }

    [Fact]
    public async Task PetroniteWebhook_MissingSecret_Returns401()
    {
        var payload = BuildPetroniteWebhookPayload("ORDER-NO-SECRET", 1, 1, 5.000m, 4000.00m, 800.00m);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/petronite/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        // No X-Webhook-Secret header

        var response = await _client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PetroniteWebhook_InvalidSecret_Returns401()
    {
        var payload = BuildPetroniteWebhookPayload("ORDER-BAD-SECRET", 1, 1, 5.000m, 4000.00m, 800.00m);

        var response = await PostPetroniteWebhookAsync(payload, "wrong-secret");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PetroniteWebhook_EmptyBody_Returns200WithIgnored()
    {
        var response = await PostPetroniteWebhookAsync("", PetroniteWebhookSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("IGNORED");
    }

    // ── Advatec Webhook Tests (ADV-6.2) ─────────────────────────────────────

    [Fact]
    public async Task AdvatecWebhook_ValidReceipt_ReturnsAccepted()
    {
        var payload = BuildAdvatecReceiptPayload("TRSD1INV001", "abc12345678", 10.0m, 32850.00m, 3285.00m);

        var response = await PostAdvatecWebhookAsync(payload, AdvatecWebhookToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ACCEPTED");
        body.GetProperty("transactionId").GetGuid().Should().NotBeEmpty();

        // Verify stored in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var stored = await db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.SiteCode == AdvatecSiteCode);

        stored.Should().NotBeNull();
        stored!.Status.Should().Be(TransactionStatus.PENDING);
        stored.FccVendor.Should().Be(FccVendor.ADVATEC);
    }

    [Fact]
    public async Task AdvatecWebhook_DuplicateReceipt_ReturnsDuplicate()
    {
        var payload = BuildAdvatecReceiptPayload("TRSD1INV-DEDUP-001", "dedup1234567", 5.0m, 16425.00m, 3285.00m);

        var first = await PostAdvatecWebhookAsync(payload, AdvatecWebhookToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        firstBody.GetProperty("status").GetString().Should().Be("ACCEPTED");

        var second = await PostAdvatecWebhookAsync(payload, AdvatecWebhookToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        secondBody.GetProperty("status").GetString().Should().Be("DUPLICATE");
    }

    [Fact]
    public async Task AdvatecWebhook_MissingToken_Returns401()
    {
        var payload = BuildAdvatecReceiptPayload("TRSD1INV-NOAUTH", "noauth123456", 5.0m, 16425.00m, 3285.00m);

        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/advatec/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        // No X-Webhook-Token header and no ?token= query parameter

        var response = await _client.SendAsync(message);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("MISSING_WEBHOOK_TOKEN");
    }

    [Fact]
    public async Task AdvatecWebhook_InvalidToken_Returns401()
    {
        var payload = BuildAdvatecReceiptPayload("TRSD1INV-BADTOKEN", "badtoken12345", 5.0m, 16425.00m, 3285.00m);

        var response = await PostAdvatecWebhookAsync(payload, "wrong-advatec-token");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("INVALID_WEBHOOK_TOKEN");
    }

    [Fact]
    public async Task AdvatecWebhook_EmptyBody_Returns200WithIgnored()
    {
        var response = await PostAdvatecWebhookAsync("", AdvatecWebhookToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("IGNORED");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> PostAdvatecWebhookAsync(string json, string webhookToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/advatec/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        message.Headers.Add("X-Webhook-Token", webhookToken);

        return await _client.SendAsync(message);
    }

    private async Task<HttpResponseMessage> PostRadixXmlAsync(string xml, int usnCode)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/radix")
        {
            Content = new StringContent(xml, Encoding.UTF8, "text/xml")
        };
        message.Headers.Add("X-Usn-Code", usnCode.ToString());

        return await _client.SendAsync(message);
    }

    private async Task<HttpResponseMessage> PostPetroniteWebhookAsync(string json, string webhookSecret)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ingest/petronite/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        message.Headers.Add("X-Webhook-Secret", webhookSecret);

        return await _client.SendAsync(message);
    }

    private static string BuildRadixTransactionXml(
        string fdcNum, string fdcSaveNum,
        string volume, string amount, string unitPrice)
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <FDC_RESP>
                <TRN FDC_NUM="{fdcNum}" FDC_SAVE_NUM="{fdcSaveNum}"
                     PUMP_ADDR="1" FP="1" NOZZLE="1"
                     PRODUCT_CODE="PMS" PRODUCT_NAME="Premium"
                     VOLUME="{volume}" AMOUNT="{amount}" UNIT_PRICE="{unitPrice}"
                     START_TIME="2026-03-11 13:50:00" END_TIME="2026-03-11 13:54:50"
                     TOKEN="0" />
            </FDC_RESP>
            """;
    }

    private static string BuildPetroniteWebhookPayload(
        string orderId, int pumpNumber, int nozzleNumber,
        decimal volumeLitres, decimal amountMajor, decimal unitPrice)
    {
        return JsonSerializer.Serialize(new
        {
            eventType = "transaction.completed",
            transaction = new
            {
                orderId,
                pumpNumber,
                nozzleNumber,
                productCode = "PMS",
                volumeLitres,
                amountMajor,
                unitPrice,
                startTime = "2026-03-11T13:50:00Z",
                endTime = "2026-03-11T13:54:50Z",
                receiptCode = "RC-001",
                attendantId = "ATT-01"
            }
        });
    }

    private static string BuildAdvatecReceiptPayload(
        string transactionId, string receiptCode,
        decimal quantity, decimal amountInclusive, decimal unitPrice)
    {
        decimal taxRate = 0.18m;
        decimal amountExclusive = Math.Round(amountInclusive / (1 + taxRate), 2);
        decimal taxAmount = amountInclusive - amountExclusive;

        return JsonSerializer.Serialize(new
        {
            DataType = "Receipt",
            Data = new
            {
                TransactionId = transactionId,
                ReceiptCode = receiptCode,
                Date = "2026-03-13",
                Time = "14:30:00",
                AmountInclusive = amountInclusive,
                AmountExclusive = amountExclusive,
                TotalTaxAmount = taxAmount,
                Discount = 0m,
                CustIdType = 1,
                CustomerId = "100-999-888",
                CustomerName = "Test Customer",
                ReceiptVCodeURL = $"https://virtual.tra.go.tz/efdmsrctverify/{receiptCode}_143000",
                ZNumber = "20260313",
                DailyCount = 1,
                GlobalCount = 1,
                Items = new[]
                {
                    new
                    {
                        Price = unitPrice,
                        Amount = amountInclusive,
                        TaxCode = "1",
                        Quantity = quantity,
                        TaxAmount = taxAmount,
                        Product = "TANGO",
                        TaxId = "A-18.00",
                        DiscountAmount = 0m,
                        TaxRate = 18.00m,
                    }
                },
                Payments = new[]
                {
                    new
                    {
                        PaymentType = "CASH",
                        PaymentAmount = amountInclusive,
                    }
                },
                Company = new
                {
                    TIN = "100-123-456",
                    VRN = "10-0123456-B",
                    Name = "ADVATECH COMPANY LIMITED",
                    City = "DAR ES SALAAM",
                    Region = "DAR ES SALAAM",
                    Street = "MSIMBAZI",
                    Country = "TZ",
                    Mobile = "+255712345678",
                    TaxOffice = "ILALA TAX REGION",
                    SerialNumber = "10TZ100625",
                    RegistrationId = "TZ0100-0000625",
                    UIN = "WEB0625",
                }
            }
        });
    }

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        var legalEntityId = Guid.Parse("aa000000-0000-0000-0000-000000000001");
        if (await db.LegalEntities.IgnoreQueryFilters().AnyAsync(e => e.Id == legalEntityId)) return;

        var radixSiteId = Guid.Parse("aa000000-0000-0000-0000-000000000002");
        var petroniteSiteId = Guid.Parse("aa000000-0000-0000-0000-000000000003");
        var advatecSiteId = Guid.Parse("aa000000-0000-0000-0000-000000000006");

        db.LegalEntities.Add(new LegalEntity
        {
            Id = legalEntityId,
            BusinessCode = "GH-001",
            CountryCode = "GH",
            CountryName = "Ghana",
            Name = "Test Vendor Push Ltd",
            CurrencyCode = "GHS",
            TaxAuthorityCode = "GRA",
            FiscalizationRequired = false,
            DefaultTimezone = "Africa/Accra",
            OdooCompanyId = "ODOO-GH-001",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        // Radix site
        db.Sites.Add(new Site
        {
            Id = radixSiteId,
            LegalEntityId = legalEntityId,
            SiteCode = RadixSiteCode,
            SiteName = "Radix Test Station",
            OperatingModel = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-RX",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.FccConfigs.Add(new FccConfig
        {
            Id = Guid.Parse("aa000000-0000-0000-0000-000000000004"),
            SiteId = radixSiteId,
            LegalEntityId = legalEntityId,
            FccVendor = FccVendor.RADIX,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress = "127.0.0.1",
            Port = 9001,
            CredentialRef = "radix-cred",
            IngestionMethod = IngestionMethod.PUSH,
            IngestionMode = IngestionMode.CLOUD_DIRECT,
            SharedSecret = RadixSharedSecret,
            UsnCode = RadixUsnCode,
            AuthPort = 9000,
            IsActive = true,
            ConfigVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        // Petronite site
        db.Sites.Add(new Site
        {
            Id = petroniteSiteId,
            LegalEntityId = legalEntityId,
            SiteCode = PetroniteSiteCode,
            SiteName = "Petronite Test Station",
            OperatingModel = SiteOperatingModel.CODO,
            CompanyTaxPayerId = "TAX-PN",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.FccConfigs.Add(new FccConfig
        {
            Id = Guid.Parse("aa000000-0000-0000-0000-000000000005"),
            SiteId = petroniteSiteId,
            LegalEntityId = legalEntityId,
            FccVendor = FccVendor.PETRONITE,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress = "127.0.0.1",
            Port = 9002,
            CredentialRef = "petronite-cred",
            IngestionMethod = IngestionMethod.PUSH,
            IngestionMode = IngestionMode.CLOUD_DIRECT,
            WebhookSecret = PetroniteWebhookSecret,
            WebhookSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PetroniteWebhookSecret))).ToLowerInvariant(),
            ClientId = "test-client-id",
            ClientSecret = "test-client-secret",
            OAuthTokenEndpoint = "http://localhost/oauth/token",
            IsActive = true,
            ConfigVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        // Advatec site (ADV-6.2)
        db.Sites.Add(new Site
        {
            Id = advatecSiteId,
            LegalEntityId = legalEntityId,
            SiteCode = AdvatecSiteCode,
            SiteName = "Advatec Test Station",
            OperatingModel = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-ADV",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.FccConfigs.Add(new FccConfig
        {
            Id = Guid.Parse("aa000000-0000-0000-0000-000000000007"),
            SiteId = advatecSiteId,
            LegalEntityId = legalEntityId,
            FccVendor = FccVendor.ADVATEC,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress = "127.0.0.1",
            Port = 5560,
            CredentialRef = "advatec-cred",
            IngestionMethod = IngestionMethod.PUSH,
            IngestionMode = IngestionMode.CLOUD_DIRECT,
            AdvatecWebhookToken = AdvatecWebhookToken,
            AdvatecWebhookTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AdvatecWebhookToken))).ToLowerInvariant(),
            AdvatecDevicePort = 5560,
            AdvatecEfdSerialNumber = "10TZ100625",
            AdvatecCustIdType = 1,
            IsActive = true,
            ConfigVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }
}
