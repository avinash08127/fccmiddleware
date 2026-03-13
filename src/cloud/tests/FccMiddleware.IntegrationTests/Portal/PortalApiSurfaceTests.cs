using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Contracts.Reconciliation;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Models;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.Portal;

[Collection("Integration")]
public sealed class PortalApiSurfaceTests : IAsyncLifetime
{
    private const string TestPortalSigningKey = "TestSigningKey-Portal-Integration-256bits!!!!!";
    private const string TestPortalIssuer = "https://login.microsoftonline.com/test-tenant-id/v2.0";
    private const string TestPortalAudience = "00000000-0000-0000-0000-000000000123";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Guid LegalEntityId = Guid.Parse("91000000-0000-0000-0000-000000000001");
    private static readonly Guid SiteId = Guid.Parse("91000000-0000-0000-0000-000000000002");
    private static readonly Guid FccConfigId = Guid.Parse("91000000-0000-0000-0000-000000000003");
    private static readonly Guid ProductId = Guid.Parse("91000000-0000-0000-0000-000000000004");
    private static readonly Guid PumpId = Guid.Parse("91000000-0000-0000-0000-000000000005");
    private static readonly Guid NozzleId = Guid.Parse("91000000-0000-0000-0000-000000000006");
    private static readonly Guid AgentId = Guid.Parse("91000000-0000-0000-0000-000000000007");
    private static readonly Guid AuditEventId = Guid.Parse("91000000-0000-0000-0000-000000000008");
    private static readonly Guid DeadLetterId = Guid.Parse("91000000-0000-0000-0000-000000000009");
    private static readonly Guid TransactionId = Guid.Parse("91000000-0000-0000-0000-000000000010");
    private static readonly Guid ReconciliationId = Guid.Parse("91000000-0000-0000-0000-000000000011");

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
                        ["PortalJwt:SigningKey"] = TestPortalSigningKey,
                        ["PortalJwt:Authority"] = TestPortalIssuer,
                        ["PortalJwt:Audience"] = TestPortalAudience,
                        ["PortalJwt:ClientId"] = TestPortalAudience
                    });
                });
            });

        _ = _factory.Server;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);

        _client = _factory.CreateClient();
        SetPortalAuth("SystemAdmin", "portal-admin", LegalEntityId);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    [Fact]
    public async Task PortalReadApis_ReturnExpectedOperationalData()
    {
        var agents = await _client.GetFromJsonAsync<PortalPagedResult<AgentHealthSummaryDto>>(
            $"/api/v1/agents?legalEntityId={LegalEntityId}&pageSize=50");
        agents.Should().NotBeNull();
        agents!.Data.Should().ContainSingle(item => item.DeviceId == AgentId);

        var telemetry = await _client.GetFromJsonAsync<AgentTelemetryDto>($"/api/v1/agents/{AgentId}/telemetry");
        telemetry.Should().NotBeNull();
        telemetry!.ConnectivityState.Should().Be(ConnectivityState.FULLY_ONLINE.ToString());
        telemetry.SequenceNumber.Should().Be(42);
        telemetry.Device.DeviceModel.Should().Be("Honeywell CT45");
        telemetry.Buffer.PendingUploadCount.Should().Be(2);

        var agentEvents = await _client.GetFromJsonAsync<List<AgentAuditEventDto>>($"/api/v1/agents/{AgentId}/events?limit=10");
        agentEvents.Should().NotBeNull();
        agentEvents!.Should().ContainSingle(item => item.Id == AuditEventId);

        var audit = await _client.GetFromJsonAsync<PortalPagedResult<AuditEventDto>>(
            $"/api/v1/audit/events?legalEntityId={LegalEntityId}&pageSize=20");
        audit.Should().NotBeNull();
        audit!.Data.Should().ContainSingle(item => item.EventId == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        var auditDetail = await _client.GetFromJsonAsync<AuditEventDto>($"/api/v1/audit/events/{AuditEventId}");
        auditDetail.Should().NotBeNull();
        auditDetail!.LegalEntityId.Should().Be(LegalEntityId);

        var legalEntities = await _client.GetFromJsonAsync<List<PortalLegalEntityDto>>("/api/v1/master-data/legal-entities");
        legalEntities.Should().NotBeNull();
        legalEntities!.Should().ContainSingle(item =>
            item.Id == LegalEntityId
            && item.Code == "LS-001"
            && item.CountryCode == "LS"
            && item.CountryName == "Lesotho"
            && item.OdooCompanyId == "ODOO-LS-001");

        var products = await _client.GetFromJsonAsync<List<ProductDto>>($"/api/v1/master-data/products?legalEntityId={LegalEntityId}");
        products.Should().NotBeNull();
        products!.Should().ContainSingle(item => item.Id == ProductId);

        var syncStatus = await _client.GetFromJsonAsync<List<MasterDataSyncStatusDto>>("/api/v1/master-data/sync-status");
        syncStatus.Should().NotBeNull();
        syncStatus!.Should().HaveCount(5);

        var summary = await _client.GetFromJsonAsync<DashboardSummaryDto>($"/api/v1/admin/dashboard/summary?legalEntityId={LegalEntityId}");
        summary.Should().NotBeNull();
        summary!.AgentStatus.TotalAgents.Should().Be(1);
        summary.TransactionVolume.HourlyBuckets.Should().HaveCount(24);

        var alerts = await _client.GetFromJsonAsync<DashboardAlertsResponseDto>($"/api/v1/admin/dashboard/alerts?legalEntityId={LegalEntityId}");
        alerts.Should().NotBeNull();
        alerts!.TotalCount.Should().BeGreaterThan(0);

        var transactions = await _client.GetFromJsonAsync<PortalPagedResult<PortalTransactionDto>>(
            $"/api/v1/ops/transactions?legalEntityId={LegalEntityId}&pageSize=20");
        transactions.Should().NotBeNull();
        transactions!.Data.Should().ContainSingle(item => item.Id == TransactionId);

        var transactionDetail = await _client.GetFromJsonAsync<PortalTransactionDto>($"/api/v1/ops/transactions/{TransactionId}");
        transactionDetail.Should().NotBeNull();
        transactionDetail!.LegalEntityId.Should().Be(LegalEntityId);

        var reconciliation = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/ops/reconciliation/exceptions?legalEntityId={LegalEntityId}&pageSize=20");
        reconciliation.GetProperty("data").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .Should().Contain(ReconciliationId);

        var reconciliationDetail = await _client.GetFromJsonAsync<ReconciliationRecordDto>(
            $"/api/v1/ops/reconciliation/{ReconciliationId}");
        reconciliationDetail.Should().NotBeNull();
        reconciliationDetail!.Id.Should().Be(ReconciliationId);
    }

    [Fact]
    public async Task SiteApis_SupportListDetailAndMutations()
    {
        var list = await _client.GetFromJsonAsync<PortalPagedResult<SiteDto>>(
            $"/api/v1/sites?legalEntityId={LegalEntityId}&pageSize=20");
        list.Should().NotBeNull();
        list!.Data.Should().ContainSingle(item => item.Id == SiteId);
        list.Data[0].SiteUsesPreAuth.Should().BeFalse();

        var detail = await _client.GetFromJsonAsync<SiteDetailDto>($"/api/v1/sites/{SiteId}");
        detail.Should().NotBeNull();
        detail!.Fcc.Should().NotBeNull();
        detail.SiteUsesPreAuth.Should().BeFalse();

        var patchResponse = await _client.PatchAsJsonAsync(
            $"/api/v1/sites/{SiteId}",
            new UpdateSiteRequestDto
            {
                ConnectivityMode = "DISCONNECTED",
                OperatingModel = "CODO",
                SiteUsesPreAuth = true,
                Tolerance = new SiteTolerancePatchDto
                {
                    AmountTolerancePct = 7.5m,
                    AmountToleranceAbsoluteMinorUnits = 999,
                    TimeWindowMinutes = 75
                },
                Fiscalization = new SiteFiscalizationPatchDto
                {
                    Mode = "EXTERNAL_INTEGRATION",
                    TaxAuthorityEndpoint = "https://tax.example.test",
                    RequireCustomerTaxId = true,
                    FiscalReceiptRequired = true
                }
            });
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var patched = await patchResponse.Content.ReadFromJsonAsync<SiteDetailDto>();
        patched.Should().NotBeNull();
        patched!.SiteUsesPreAuth.Should().BeTrue();

        var updateFccResponse = await _client.PutAsJsonAsync(
            $"/api/v1/sites/{SiteId}/fcc-config",
            new UpdateFccConfigRequestDto
            {
                Enabled = true,
                Vendor = "DOMS",
                ConnectionProtocol = "REST",
                HostAddress = "10.20.30.40",
                Port = 9090,
                TransactionMode = "PULL",
                IngestionMode = "RELAY",
                PullIntervalSeconds = 45,
                HeartbeatIntervalSeconds = 30,
                HeartbeatTimeoutSeconds = 90
            });
        updateFccResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var addPumpResponse = await _client.PostAsJsonAsync(
            $"/api/v1/sites/{SiteId}/pumps",
            new AddPumpRequestDto
            {
                PumpNumber = 2,
                FccPumpNumber = 20,
                Nozzles =
                [
                    new AddNozzleRequestDto
                    {
                        NozzleNumber = 1,
                        FccNozzleNumber = 10,
                        CanonicalProductCode = "ULP95"
                    }
                ]
            });
        addPumpResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var addedPump = await addPumpResponse.Content.ReadFromJsonAsync<PumpDto>();
        addedPump.Should().NotBeNull();

        var updateNozzleResponse = await _client.PatchAsJsonAsync(
            $"/api/v1/sites/{SiteId}/pumps/{PumpId}/nozzles/1",
            new UpdateNozzleRequestDto { CanonicalProductCode = "ULP95" });
        updateNozzleResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var deletePumpResponse = await _client.DeleteAsync($"/api/v1/sites/{SiteId}/pumps/{addedPump!.Id}");
        deletePumpResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task SettingsApis_ReadAndPersistUpdates()
    {
        var settings = await _client.GetFromJsonAsync<SystemSettingsDto>("/api/v1/admin/settings");
        settings.Should().NotBeNull();

        var updateDefaults = await _client.PutAsJsonAsync(
            "/api/v1/admin/settings/global-defaults",
            new UpdateGlobalDefaultsRequestDto
            {
                Tolerance = new PartialToleranceDefaultsDto { StalePendingThresholdDays = 9 },
                Retention = new PartialRetentionDefaultsDto { DeadLetterRetentionDays = 45 }
            });
        updateDefaults.StatusCode.Should().Be(HttpStatusCode.OK);

        var upsertOverride = await _client.PutAsJsonAsync(
            $"/api/v1/admin/settings/overrides/{LegalEntityId}",
            new UpsertLegalEntityOverrideRequestDto
            {
                LegalEntityId = LegalEntityId,
                AmountTolerancePercent = 8.25m,
                AmountToleranceAbsoluteMinorUnits = 111,
                TimeWindowMinutes = 80,
                StalePendingThresholdDays = 12
            });
        upsertOverride.StatusCode.Should().Be(HttpStatusCode.OK);

        var updateAlerts = await _client.PutAsJsonAsync(
            "/api/v1/admin/settings/alerts",
            new UpdateAlertConfigurationRequestDto
            {
                Thresholds =
                [
                    new AlertThresholdPatchDto { AlertKey = "dlq_depth", Threshold = 2, EvaluationWindowMinutes = 10 }
                ],
                EmailRecipientsHigh = ["ops@example.test"],
                EmailRecipientsCritical = ["critical@example.test"],
                RenotifyIntervalHours = 2,
                AutoResolveHealthyCount = 4
            });
        updateAlerts.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleteOverride = await _client.DeleteAsync($"/api/v1/admin/settings/overrides/{LegalEntityId}");
        deleteOverride.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateFccConfig_RejectsUnsupportedVendor()
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/v1/sites/{SiteId}/fcc-config",
            new UpdateFccConfigRequestDto
            {
                Enabled = true,
                Vendor = "ADVATEC",
                ConnectionProtocol = "REST",
                HostAddress = "10.20.30.40",
                Port = 9090,
                TransactionMode = "PULL",
                IngestionMode = "RELAY",
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("VALIDATION.UNSUPPORTED_FCC_VENDOR");
    }

    [Fact]
    public async Task DlqApis_ListDetailAndMutate()
    {
        var list = await _client.GetFromJsonAsync<PortalPagedResult<DeadLetterDto>>(
            $"/api/v1/dlq?legalEntityId={LegalEntityId}&pageSize=20");
        list.Should().NotBeNull();
        list!.Data.Should().ContainSingle(item => item.Id == DeadLetterId);

        var detail = await _client.GetFromJsonAsync<DeadLetterDetailDto>($"/api/v1/dlq/{DeadLetterId}");
        detail.Should().NotBeNull();
        detail!.RetryHistory.Should().BeEmpty();

        var retryResponse = await _client.PostAsJsonAsync($"/api/v1/dlq/{DeadLetterId}/retry", new { });
        retryResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var discardBatchResponse = await _client.PostAsJsonAsync(
            "/api/v1/dlq/discard-batch",
            new DiscardBatchRequestDto
            {
                Items =
                [
                    new BatchDiscardItemDto
                    {
                        Id = DeadLetterId,
                        Reason = "Not required anymore."
                    }
                ]
            });
        discardBatchResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var retryBatchResponse = await _client.PostAsJsonAsync(
            "/api/v1/dlq/retry-batch",
            new RetryBatchRequestDto { Ids = [DeadLetterId] });
        retryBatchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private void SetPortalAuth(string role, string oid, params Guid[] legalEntityIds)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(TestPortalSigningKey);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, oid),
            new("oid", oid),
            new("roles", role)
        };

        claims.Add(new Claim("legal_entities", string.Join(",", legalEntityIds)));

        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = TestPortalIssuer,
            Audience = TestPortalAudience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
        });

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenHandler.WriteToken(token));
    }

    private static async Task SeedAsync(FccMiddlewareDbContext db)
    {
        var now = DateTimeOffset.UtcNow;

        db.LegalEntities.Add(new LegalEntity
        {
            Id = LegalEntityId,
            BusinessCode = "LS-001",
            CountryCode = "LS",
            CountryName = "Lesotho",
            Name = "Lesotho Test",
            CurrencyCode = "LSL",
            TaxAuthorityCode = "RSL",
            DefaultTimezone = "Africa/Maseru",
            FiscalizationRequired = true,
            OdooCompanyId = "ODOO-LS-001",
            AmountTolerancePercent = 5,
            AmountToleranceAbsolute = 500,
            TimeWindowMinutes = 60,
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Products.Add(new Product
        {
            Id = ProductId,
            LegalEntityId = LegalEntityId,
            ProductCode = "ULP95",
            ProductName = "Unleaded 95",
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Sites.Add(new Site
        {
            Id = SiteId,
            LegalEntityId = LegalEntityId,
            SiteCode = "SITE-001",
            SiteName = "Portal Test Site",
            OperatingModel = SiteOperatingModel.COCO,
            SiteUsesPreAuth = false,
            ConnectivityMode = "CONNECTED",
            CompanyTaxPayerId = "COMP-123",
            FiscalizationMode = FiscalizationMode.FCC_DIRECT,
            TaxAuthorityEndpoint = "https://tax.example.test",
            RequireCustomerTaxId = false,
            FiscalReceiptRequired = true,
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.FccConfigs.Add(new FccConfig
        {
            Id = FccConfigId,
            SiteId = SiteId,
            LegalEntityId = LegalEntityId,
            FccVendor = FccVendor.DOMS,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress = "127.0.0.1",
            Port = 8080,
            CredentialRef = "secret://fcc",
            IngestionMethod = IngestionMethod.PUSH,
            IngestionMode = IngestionMode.CLOUD_DIRECT,
            PullIntervalSeconds = 30,
            HeartbeatIntervalSeconds = 60,
            HeartbeatTimeoutSeconds = 180,
            IsActive = true,
            ConfigVersion = 1,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Pumps.Add(new Pump
        {
            Id = PumpId,
            SiteId = SiteId,
            LegalEntityId = LegalEntityId,
            PumpNumber = 1,
            FccPumpNumber = 10,
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            Nozzles =
            [
                new Nozzle
                {
                    Id = NozzleId,
                    SiteId = SiteId,
                    LegalEntityId = LegalEntityId,
                    OdooNozzleNumber = 1,
                    FccNozzleNumber = 10,
                    ProductId = ProductId,
                    IsActive = true,
                    SyncedAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                }
            ]
        });

        db.AgentRegistrations.Add(new AgentRegistration
        {
            Id = AgentId,
            SiteId = SiteId,
            LegalEntityId = LegalEntityId,
            SiteCode = "SITE-001",
            DeviceSerialNumber = "SER-001",
            DeviceModel = "Honeywell CT45",
            OsVersion = "Android 14",
            AgentVersion = "1.2.3",
            IsActive = true,
            TokenHash = "hash",
            TokenExpiresAt = now.AddHours(1),
            LastSeenAt = now,
            RegisteredAt = now.AddDays(-2),
            CreatedAt = now,
            UpdatedAt = now
        });

        var telemetryPayload = new TelemetryPayload
        {
            SchemaVersion = "1.0",
            DeviceId = AgentId,
            SiteCode = "SITE-001",
            LegalEntityId = LegalEntityId,
            ReportedAtUtc = now,
            SequenceNumber = 42,
            ConnectivityState = ConnectivityState.FULLY_ONLINE,
            Device = new DeviceStatus
            {
                BatteryPercent = 82,
                IsCharging = true,
                StorageFreeMb = 2048,
                StorageTotalMb = 4096,
                MemoryFreeMb = 512,
                MemoryTotalMb = 1024,
                AppVersion = "1.2.3",
                AppUptimeSeconds = 7200,
                OsVersion = "Android 14",
                DeviceModel = "Honeywell CT45"
            },
            FccHealth = new FccHealthStatus
            {
                IsReachable = true,
                LastHeartbeatAtUtc = now.AddSeconds(-30),
                HeartbeatAgeSeconds = 30,
                FccVendor = FccVendor.DOMS,
                FccHost = "127.0.0.1",
                FccPort = 8080,
                ConsecutiveHeartbeatFailures = 0
            },
            Buffer = new BufferStatus
            {
                TotalRecords = 10,
                PendingUploadCount = 2,
                SyncedCount = 7,
                SyncedToOdooCount = 1,
                FailedCount = 0,
                OldestPendingAtUtc = now.AddMinutes(-5),
                BufferSizeMb = 12
            },
            Sync = new SyncStatus
            {
                LastSyncAttemptUtc = now.AddMinutes(-1),
                LastSuccessfulSyncUtc = now.AddMinutes(-1),
                SyncLagSeconds = 60,
                LastStatusPollUtc = now.AddMinutes(-1),
                LastConfigPullUtc = now.AddMinutes(-10),
                ConfigVersion = "1",
                UploadBatchSize = 50
            },
            ErrorCounts = new ErrorCounts()
        };

        db.AgentTelemetrySnapshots.Add(new AgentTelemetrySnapshot
        {
            DeviceId = AgentId,
            LegalEntityId = LegalEntityId,
            SiteCode = "SITE-001",
            ReportedAtUtc = now,
            ConnectivityState = ConnectivityState.FULLY_ONLINE,
            PayloadJson = JsonSerializer.Serialize(TelemetrySnapshotPayload.FromTelemetry(telemetryPayload), JsonOptions),
            BatteryPercent = 82,
            IsCharging = true,
            PendingUploadCount = 2,
            SyncLagSeconds = 60,
            LastHeartbeatAtUtc = now.AddSeconds(-30),
            HeartbeatAgeSeconds = 30,
            FccVendor = FccVendor.DOMS,
            FccHost = "127.0.0.1",
            FccPort = 8080,
            ConsecutiveHeartbeatFailures = 0,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.AuditEvents.Add(new AuditEvent
        {
            Id = AuditEventId,
            CreatedAt = now,
            LegalEntityId = LegalEntityId,
            EventType = "AgentRegistered",
            CorrelationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            SiteCode = "SITE-001",
            Source = "portal-test",
            Payload = """
                {"eventId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","eventType":"AgentRegistered","schemaVersion":1,"timestamp":"2026-03-12T00:00:00Z","source":"portal-test","correlationId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","legalEntityId":"91000000-0000-0000-0000-000000000001","siteCode":"SITE-001","payload":{"deviceId":"91000000-0000-0000-0000-000000000007","message":"Agent registered"}} 
                """
        });

        db.DeadLetterItems.Add(new DeadLetterItem
        {
            Id = DeadLetterId,
            LegalEntityId = LegalEntityId,
            SiteCode = "SITE-001",
            Type = DeadLetterType.TRANSACTION,
            FccTransactionId = "FCC-123",
            RawPayloadRef = "s3://bucket/raw/FCC-123.json",
            RawPayloadJson = """{"fccTransactionId":"FCC-123"}""",
            FailureReason = DeadLetterReason.ADAPTER_ERROR,
            ErrorCode = "ADAPTER.INVALID_PAYLOAD",
            ErrorMessage = "Payload could not be normalized.",
            Status = DeadLetterStatus.PENDING,
            RetryCount = 0,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Transactions.Add(new Transaction
        {
            Id = TransactionId,
            CreatedAt = now.AddMinutes(-30),
            LegalEntityId = LegalEntityId,
            FccTransactionId = "TX-001",
            SiteCode = "SITE-001",
            PumpNumber = 1,
            NozzleNumber = 1,
            ProductCode = "ULP95",
            VolumeMicrolitres = 1_000_000,
            AmountMinorUnits = 50000,
            UnitPriceMinorPerLitre = 50000,
            CurrencyCode = "MWK",
            StartedAt = now.AddMinutes(-31),
            CompletedAt = now.AddMinutes(-30),
            FccVendor = FccVendor.DOMS,
            Status = TransactionStatus.PENDING,
            IngestionSource = IngestionSource.FCC_PUSH,
            CorrelationId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            UpdatedAt = now
        });

        db.ReconciliationRecords.Add(new ReconciliationRecord
        {
            Id = ReconciliationId,
            LegalEntityId = LegalEntityId,
            SiteCode = "SITE-001",
            TransactionId = TransactionId,
            PumpNumber = 1,
            NozzleNumber = 1,
            ActualAmountMinorUnits = 50000,
            MatchMethod = "TIME_WINDOW",
            Status = ReconciliationStatus.VARIANCE_FLAGGED,
            LastMatchAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        await db.SaveChangesAsync();
    }
}
