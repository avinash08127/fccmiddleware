using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.Telemetry;

[Collection("Integration")]
public sealed class SubmitTelemetryTests : IAsyncLifetime
{
    private const string TestSigningKey = "TestSigningKey-Telemetry-Integration-256bits!!!!";
    private const string TestIssuer = "fcc-middleware-cloud";
    private const string TestAudience = "fcc-middleware-api";

    private static readonly Guid TestLegalEntityId = Guid.Parse("99000000-0000-0000-0000-000000000071");
    private static readonly Guid TestSiteId = Guid.Parse("99000000-0000-0000-0000-000000000072");
    private static readonly Guid TestDeviceId = Guid.Parse("99000000-0000-0000-0000-000000000073");
    private const string TestSiteCode = "TEL-SITE-001";

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
                        ["DeviceJwt:SigningKey"] = TestSigningKey,
                        ["DeviceJwt:Issuer"] = TestIssuer,
                        ["DeviceJwt:Audience"] = TestAudience
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

    [Fact]
    public async Task SubmitTelemetry_ValidPayload_Returns204_UpdatesLastSeenAndStoresAuditEvent()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateDeviceJwt(TestDeviceId.ToString(), TestSiteCode, TestLegalEntityId));

        var request = BuildTelemetryPayload(sequenceNumber: 7);

        var response = await _client.PostAsJsonAsync("/api/v1/agent/telemetry", request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var agent = await db.AgentRegistrations.IgnoreQueryFilters()
            .SingleAsync(a => a.Id == TestDeviceId);
        agent.LastSeenAt.Should().NotBeNull();

        var auditEvent = await db.AuditEvents.IgnoreQueryFilters()
            .Where(a => a.EventType == "AgentHealthReported" && a.SiteCode == TestSiteCode)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync();

        auditEvent.Should().NotBeNull();
        var payload = JsonSerializer.Deserialize<JsonElement>(auditEvent!.Payload);
        payload.GetProperty("eventType").GetString().Should().Be("AgentHealthReported");
        payload.GetProperty("data").GetProperty("sequenceNumber").GetInt32().Should().Be(7);
        payload.GetProperty("data").GetProperty("summary").GetProperty("bufferDepth").GetInt32().Should().Be(42);

        var snapshot = await db.AgentTelemetrySnapshots.IgnoreQueryFilters()
            .SingleAsync(s => s.DeviceId == TestDeviceId);
        var snapshotPayload = JsonSerializer.Deserialize<JsonElement>(snapshot.PayloadJson);
        snapshotPayload.GetProperty("format").GetString().Should().Be("supplemental-v1");
        snapshotPayload.GetProperty("sequenceNumber").GetInt32().Should().Be(7);
        snapshotPayload.TryGetProperty("reportedAtUtc", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubmitTelemetry_RepeatedReportsWithinThrottleWindow_UpdateSnapshotWithoutAddingHealthAuditRows()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateDeviceJwt(TestDeviceId.ToString(), TestSiteCode, TestLegalEntityId));

        var firstResponse = await _client.PostAsJsonAsync("/api/v1/agent/telemetry", BuildTelemetryPayload(sequenceNumber: 11));
        var secondResponse = await _client.PostAsJsonAsync("/api/v1/agent/telemetry", BuildTelemetryPayload(sequenceNumber: 12));
        var duplicateSecondResponse = await _client.PostAsJsonAsync("/api/v1/agent/telemetry", BuildTelemetryPayload(sequenceNumber: 12));

        firstResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        duplicateSecondResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();

        var healthAuditCount = await db.AuditEvents.IgnoreQueryFilters()
            .CountAsync(a => a.EventType == "AgentHealthReported" && a.EntityId == TestDeviceId);
        healthAuditCount.Should().Be(1);

        var snapshot = await db.AgentTelemetrySnapshots.IgnoreQueryFilters()
            .SingleAsync(s => s.DeviceId == TestDeviceId);
        var snapshotPayload = JsonSerializer.Deserialize<JsonElement>(snapshot.PayloadJson);
        snapshotPayload.GetProperty("sequenceNumber").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task SubmitTelemetry_InvalidPayload_Returns400WithStructuredError()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateDeviceJwt(TestDeviceId.ToString(), TestSiteCode, TestLegalEntityId));

        var request = BuildTelemetryPayload(sequenceNumber: 8);
        request["device"] = new
        {
            batteryPercent = 101,
            isCharging = true,
            storageFreeMb = 100,
            storageTotalMb = 200,
            memoryFreeMb = 100,
            memoryTotalMb = 200,
            appVersion = "1.2.3",
            appUptimeSeconds = 60,
            osVersion = "14",
            deviceModel = "Urovo"
        };

        var response = await _client.PostAsJsonAsync("/api/v1/agent/telemetry", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("VALIDATION.INVALID_PAYLOAD");
        body.TryGetProperty("details", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SubmitTelemetry_WithoutToken_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.PostAsJsonAsync("/api/v1/agent/telemetry", BuildTelemetryPayload(sequenceNumber: 9));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SubmitTelemetry_MismatchedPayloadIdentity_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateDeviceJwt(TestDeviceId.ToString(), TestSiteCode, TestLegalEntityId));

        var request = BuildTelemetryPayload(sequenceNumber: 10);
        request["siteCode"] = "OTHER-SITE";

        var response = await _client.PostAsJsonAsync("/api/v1/agent/telemetry", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("SITE_MISMATCH");
    }

    private static Dictionary<string, object?> BuildTelemetryPayload(int sequenceNumber) =>
        new()
        {
            ["schemaVersion"] = "1.0",
            ["deviceId"] = TestDeviceId,
            ["siteCode"] = TestSiteCode,
            ["legalEntityId"] = TestLegalEntityId,
            ["reportedAtUtc"] = DateTimeOffset.UtcNow,
            ["sequenceNumber"] = sequenceNumber,
            ["connectivityState"] = "FULLY_ONLINE",
            ["device"] = new
            {
                batteryPercent = 88,
                isCharging = true,
                storageFreeMb = 2048,
                storageTotalMb = 4096,
                memoryFreeMb = 512,
                memoryTotalMb = 1024,
                appVersion = "1.2.3",
                appUptimeSeconds = 3600,
                osVersion = "14",
                deviceModel = "Urovo i9100"
            },
            ["fccHealth"] = new
            {
                isReachable = true,
                lastHeartbeatAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
                heartbeatAgeSeconds = 30,
                fccVendor = "DOMS",
                fccHost = "192.168.1.10",
                fccPort = 8080,
                consecutiveHeartbeatFailures = 0
            },
            ["buffer"] = new
            {
                totalRecords = 55,
                pendingUploadCount = 42,
                syncedCount = 10,
                syncedToOdooCount = 3,
                failedCount = 0,
                oldestPendingAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                bufferSizeMb = 128
            },
            ["sync"] = new
            {
                lastSyncAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                lastSuccessfulSyncUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                syncLagSeconds = 120,
                lastStatusPollUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                lastConfigPullUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
                configVersion = "5",
                uploadBatchSize = 100
            },
            ["errorCounts"] = new
            {
                fccConnectionErrors = 0,
                cloudUploadErrors = 0,
                cloudAuthErrors = 0,
                localApiErrors = 0,
                bufferWriteErrors = 0,
                adapterNormalizationErrors = 0,
                preAuthErrors = 0
            }
        };

    private static string CreateDeviceJwt(string deviceId, string siteCode, Guid legalEntityId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTimeOffset.UtcNow;

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, deviceId),
            new Claim("site", siteCode),
            new Claim("lei", legalEntityId.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: TestIssuer,
            audience: TestAudience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.AddHours(1).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task SeedTestDataAsync(FccMiddlewareDbContext db)
    {
        db.LegalEntities.Add(new LegalEntity
        {
            Id = TestLegalEntityId,
            BusinessCode = "ZW-001",
            CountryCode = "ZW",
            CountryName = "Zimbabwe",
            Name = "Telemetry Test LE",
            CurrencyCode = "ZWL",
            TaxAuthorityCode = "ZIMRA",
            DefaultTimezone = "Africa/Harare",
            OdooCompanyId = "ODOO-ZW-001",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.Sites.Add(new Site
        {
            Id = TestSiteId,
            LegalEntityId = TestLegalEntityId,
            SiteCode = TestSiteCode,
            SiteName = "Telemetry Test Site",
            OperatingModel = SiteOperatingModel.COCO,
            CompanyTaxPayerId = "TAX-TEL-001",
            IsActive = true,
            SyncedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        db.AgentRegistrations.Add(new AgentRegistration
        {
            Id = TestDeviceId,
            SiteId = TestSiteId,
            LegalEntityId = TestLegalEntityId,
            SiteCode = TestSiteCode,
            DeviceSerialNumber = "TEL-SN-001",
            DeviceModel = "Urovo i9100",
            OsVersion = "14",
            AgentVersion = "1.2.3",
            IsActive = true,
            TokenHash = "hash",
            TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            RegisteredAt = DateTimeOffset.UtcNow.AddDays(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        await db.SaveChangesAsync();
    }
}
