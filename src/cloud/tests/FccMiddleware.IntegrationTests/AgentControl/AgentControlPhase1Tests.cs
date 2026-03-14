using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FccMiddleware.Contracts.AgentControl;
using FccMiddleware.Contracts.Portal;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using FccMiddleware.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace FccMiddleware.IntegrationTests.AgentControl;

[Collection("Integration")]
public sealed class AgentControlPhase1Tests : IAsyncLifetime
{
    private const string TestDeviceSigningKey = "TestSigningKey-AgentControl-Device-256bits!!!";
    private const string TestDeviceIssuer = "fcc-middleware-cloud";
    private const string TestDeviceAudience = "fcc-middleware-api";
    private const string TestPortalSigningKey = "TestSigningKey-AgentControl-Portal-256bits!!!";
    private const string TestPortalIssuer = "https://login.microsoftonline.com/test-tenant-id/v2.0";
    private const string TestPortalAudience = "00000000-0000-0000-0000-000000000456";
    private const string TestFieldEncryptionKey = "1111111111111111111111111111111111111111111111111111111111111111";

    private static readonly Guid LegalEntityId = Guid.Parse("92000000-0000-0000-0000-000000000001");
    private static readonly Guid CommandSiteId = Guid.Parse("92000000-0000-0000-0000-000000000002");
    private static readonly Guid RegistrationSiteId = Guid.Parse("92000000-0000-0000-0000-000000000003");
    private static readonly Guid DecommissionSiteId = Guid.Parse("92000000-0000-0000-0000-000000000004");
    private static readonly Guid CommandDeviceId = Guid.Parse("92000000-0000-0000-0000-000000000005");
    private static readonly Guid DecommissionDeviceId = Guid.Parse("92000000-0000-0000-0000-000000000006");
    private static readonly Guid CommandConfigId = Guid.Parse("92000000-0000-0000-0000-000000000007");
    private static readonly Guid RegistrationConfigId = Guid.Parse("92000000-0000-0000-0000-000000000008");
    private static readonly Guid DecommissionConfigId = Guid.Parse("92000000-0000-0000-0000-000000000009");

    private const string CommandSiteCode = "CMD-SITE-001";
    private const string RegistrationSiteCode = "REG-SITE-002";
    private const string DecommissionSiteCode = "DECOM-SITE-003";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

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
                        ["DeviceJwt:SigningKey"] = TestDeviceSigningKey,
                        ["DeviceJwt:Issuer"] = TestDeviceIssuer,
                        ["DeviceJwt:Audience"] = TestDeviceAudience,
                        ["PortalJwt:SigningKey"] = TestPortalSigningKey,
                        ["PortalJwt:Authority"] = TestPortalIssuer,
                        ["PortalJwt:Audience"] = TestPortalAudience,
                        ["PortalJwt:ClientId"] = TestPortalAudience,
                        ["FieldEncryption:Key"] = TestFieldEncryptionKey,
                        ["AgentCommands:Enabled"] = "true",
                        ["AgentCommands:FcmHintsEnabled"] = "true",
                        ["AgentCommands:DefaultCommandTtlHours"] = "24",
                        ["BootstrapTokens:HistoryApiEnabled"] = "true"
                    });
                });
            });

        _ = _factory.Server;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        await db.Database.EnsureCreatedAsync();
        await SeedAsync(db);

        _client = _factory.CreateClient();
        SetPortalAuth("FccAdmin", "portal-admin", LegalEntityId);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await Task.WhenAll(_postgres.StopAsync(), _redis.StopAsync());
    }

    [Fact]
    public async Task BootstrapTokenHistory_ReturnsComputedStatusesAndFilters()
    {
        var now = DateTimeOffset.UtcNow;
        var activeTokenId = Guid.NewGuid();
        var expiredTokenId = Guid.NewGuid();
        var usedTokenId = Guid.NewGuid();
        var revokedTokenId = Guid.NewGuid();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
            db.BootstrapTokens.AddRange(
                new BootstrapToken
                {
                    Id = activeTokenId,
                    LegalEntityId = LegalEntityId,
                    SiteCode = CommandSiteCode,
                    TokenHash = $"hash-{activeTokenId:N}",
                    Status = ProvisioningTokenStatus.ACTIVE,
                    CreatedBy = "legacy-user",
                    CreatedByActorId = "portal-admin",
                    CreatedByActorDisplay = "Portal Admin",
                    ExpiresAt = now.AddHours(4),
                    CreatedAt = now.AddHours(-4)
                },
                new BootstrapToken
                {
                    Id = expiredTokenId,
                    LegalEntityId = LegalEntityId,
                    SiteCode = CommandSiteCode,
                    TokenHash = $"hash-{expiredTokenId:N}",
                    Status = ProvisioningTokenStatus.ACTIVE,
                    CreatedBy = "legacy-expired",
                    CreatedByActorDisplay = "Expired Creator",
                    ExpiresAt = now.AddMinutes(-30),
                    CreatedAt = now.AddHours(-3)
                },
                new BootstrapToken
                {
                    Id = usedTokenId,
                    LegalEntityId = LegalEntityId,
                    SiteCode = CommandSiteCode,
                    TokenHash = $"hash-{usedTokenId:N}",
                    Status = ProvisioningTokenStatus.USED,
                    CreatedBy = "legacy-used",
                    CreatedByActorDisplay = "Used Creator",
                    ExpiresAt = now.AddHours(1),
                    UsedAt = now.AddMinutes(-15),
                    UsedByDeviceId = CommandDeviceId,
                    CreatedAt = now.AddHours(-2)
                },
                new BootstrapToken
                {
                    Id = revokedTokenId,
                    LegalEntityId = LegalEntityId,
                    SiteCode = CommandSiteCode,
                    TokenHash = $"hash-{revokedTokenId:N}",
                    Status = ProvisioningTokenStatus.REVOKED,
                    CreatedBy = "legacy-revoked",
                    CreatedByActorDisplay = "Revoked Creator",
                    ExpiresAt = now.AddHours(1),
                    RevokedAt = now.AddMinutes(-10),
                    RevokedByActorId = "ops-user",
                    RevokedByActorDisplay = "Ops User",
                    CreatedAt = now.AddHours(-1)
                });

            await db.SaveChangesAsync();
        }

        SetPortalAuth("FccAdmin", "portal-admin", LegalEntityId);

        var response = await _client.GetFromJsonAsync<PortalPagedResult<BootstrapTokenHistoryRow>>(
            $"/api/v1/admin/bootstrap-tokens?legalEntityId={LegalEntityId}&pageSize=20",
            JsonOptions);

        response.Should().NotBeNull();
        response!.Data.Should().ContainSingle(item => item.TokenId == activeTokenId && item.EffectiveStatus == ProvisioningTokenStatus.ACTIVE);
        response.Data.Should().ContainSingle(item => item.TokenId == expiredTokenId && item.EffectiveStatus == ProvisioningTokenStatus.EXPIRED);
        response.Data.Should().ContainSingle(item => item.TokenId == usedTokenId && item.EffectiveStatus == ProvisioningTokenStatus.USED && item.UsedByDeviceId == CommandDeviceId);
        response.Data.Should().ContainSingle(item => item.TokenId == revokedTokenId && item.EffectiveStatus == ProvisioningTokenStatus.REVOKED && item.RevokedByActorDisplay == "Ops User");

        var filtered = await _client.GetFromJsonAsync<PortalPagedResult<BootstrapTokenHistoryRow>>(
            $"/api/v1/admin/bootstrap-tokens?legalEntityId={LegalEntityId}&status=EXPIRED&pageSize=20",
            JsonOptions);

        filtered.Should().NotBeNull();
        filtered!.Data.Should().ContainSingle(item => item.TokenId == expiredTokenId);
    }

    [Fact]
    public async Task Register_EmitsBootstrapTokenUsedAuditEvent()
    {
        SetPortalAuth("FccAdmin", "portal-admin", LegalEntityId);

        var tokenResponse = await _client.PostAsJsonAsync(
            "/api/v1/admin/bootstrap-tokens",
            new { siteCode = RegistrationSiteCode, legalEntityId = LegalEntityId });
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var tokenBody = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var tokenId = tokenBody!.GetProperty("tokenId").GetGuid();
        var rawToken = tokenBody.GetProperty("rawToken").GetString();

        ClearAuth();

        var registerResponse = await _client.PostAsJsonAsync(
            "/api/v1/agent/register",
            new
            {
                provisioningToken = rawToken,
                siteCode = RegistrationSiteCode,
                deviceSerialNumber = "REG-DEVICE-001",
                deviceModel = "Urovo i9100",
                osVersion = "12",
                agentVersion = "1.2.3"
            });

        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var registerBody = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var deviceId = registerBody!.GetProperty("deviceId").GetGuid();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var audit = await db.AuditEvents
            .IgnoreQueryFilters()
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(item =>
                item.EventType == AgentControlAuditEventTypes.BootstrapTokenUsed
                && item.EntityId == tokenId);

        audit.Should().NotBeNull();
        audit!.Payload.Should().Contain(tokenId.ToString());
        audit.Payload.Should().Contain(deviceId.ToString());
        audit.Payload.Should().NotContain(rawToken!);
    }

    [Fact]
    public async Task CreateCommand_PollAndAck_AreIdempotent()
    {
        SetPortalAuth("FccAdmin", "portal-admin", LegalEntityId);

        var createResponse = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{CommandDeviceId}/commands",
            new CreateAgentCommandRequest
            {
                CommandType = AgentCommandType.FORCE_CONFIG_PULL,
                Reason = "Need an immediate config refresh",
                Payload = JsonSerializer.SerializeToElement(new { configVersion = 2 })
            });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateAgentCommandResponse>(JsonOptions);
        created.Should().NotBeNull();
        created!.Status.Should().Be(AgentCommandStatus.PENDING);

        SetDeviceAuth(CommandDeviceId, CommandSiteCode, LegalEntityId);

        var pollResponse = await _client.GetFromJsonAsync<EdgeCommandPollResponse>("/api/v1/agent/commands", JsonOptions);
        pollResponse.Should().NotBeNull();
        pollResponse!.Commands.Should().ContainSingle(item => item.CommandId == created.CommandId);

        var ackResponse = await _client.PostAsJsonAsync(
            $"/api/v1/agent/commands/{created.CommandId}/ack",
            new CommandAckRequest
            {
                CompletionStatus = AgentCommandCompletionStatus.ACKED,
                Result = JsonSerializer.SerializeToElement(new { applied = true })
            });

        ackResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var acked = await ackResponse.Content.ReadFromJsonAsync<CommandAckResponse>(JsonOptions);
        acked.Should().NotBeNull();
        acked!.Duplicate.Should().BeFalse();
        acked.Status.Should().Be(AgentCommandStatus.ACKED);

        var duplicateAckResponse = await _client.PostAsJsonAsync(
            $"/api/v1/agent/commands/{created.CommandId}/ack",
            new CommandAckRequest
            {
                CompletionStatus = AgentCommandCompletionStatus.ACKED,
                Result = JsonSerializer.SerializeToElement(new { applied = true })
            });

        duplicateAckResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var duplicate = await duplicateAckResponse.Content.ReadFromJsonAsync<CommandAckResponse>(JsonOptions);
        duplicate.Should().NotBeNull();
        duplicate!.Duplicate.Should().BeTrue();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var command = await db.AgentCommands.IgnoreQueryFilters().FirstAsync(item => item.Id == created.CommandId);
        command.Status.Should().Be(AgentCommandStatus.ACKED);

        var createdAuditCount = await db.AuditEvents.IgnoreQueryFilters()
            .CountAsync(item => item.EventType == AgentControlAuditEventTypes.AgentCommandCreated && item.EntityId == CommandDeviceId);
        var ackAuditCount = await db.AuditEvents.IgnoreQueryFilters()
            .CountAsync(item => item.EventType == AgentControlAuditEventTypes.AgentCommandAcked && item.EntityId == CommandDeviceId);

        createdAuditCount.Should().BeGreaterThan(0);
        ackAuditCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DecommissionCommand_AndAuthEnforcement_CancelPendingCommand()
    {
        SetPortalAuth("FccAdmin", "portal-admin", LegalEntityId);

        var createResponse = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{DecommissionDeviceId}/commands",
            new CreateAgentCommandRequest
            {
                CommandType = AgentCommandType.DECOMMISSION,
                Reason = "Replacement hardware is arriving onsite"
            });

        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateAgentCommandResponse>(JsonOptions);
        created.Should().NotBeNull();

        SetDeviceAuth(DecommissionDeviceId, DecommissionSiteCode, LegalEntityId);
        var pollResponse = await _client.GetFromJsonAsync<EdgeCommandPollResponse>("/api/v1/agent/commands", JsonOptions);
        pollResponse.Should().NotBeNull();
        pollResponse!.Commands.Should().ContainSingle(item => item.CommandId == created!.CommandId && item.CommandType == AgentCommandType.DECOMMISSION);

        SetPortalAuth("FccAdmin", "portal-admin", LegalEntityId);
        var decommissionResponse = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agent/{DecommissionDeviceId}/decommission",
            new { reason = "Device retired and replaced by new unit." });
        decommissionResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        SetDeviceAuth(DecommissionDeviceId, DecommissionSiteCode, LegalEntityId);
        var blockedResponse = await _client.GetAsync("/api/v1/agent/commands");
        blockedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var command = await db.AgentCommands.IgnoreQueryFilters().FirstAsync(item => item.Id == created!.CommandId);
        command.Status.Should().Be(AgentCommandStatus.CANCELLED);

        var cancelAudit = await db.AuditEvents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(item =>
                item.EventType == AgentControlAuditEventTypes.AgentCommandCancelled
                && item.EntityId == DecommissionDeviceId);
        cancelAudit.Should().NotBeNull();
    }

    [Fact]
    public async Task AndroidInstallationUpsert_RequiresAuth_StoresCiphertext_AndRedactsAudit()
    {
        ClearAuth();
        var anonymousResponse = await _client.PostAsJsonAsync(
            "/api/v1/agent/installations/android",
            new AndroidInstallationUpsertRequest
            {
                InstallationId = Guid.NewGuid(),
                RegistrationToken = "fcm-anon-token-1234567890",
                AppVersion = "1.0.0",
                OsVersion = "13",
                DeviceModel = "Urovo i9100"
            });
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var installationId = Guid.NewGuid();
        var registrationToken = "fcm-live-token-1234567890";

        SetDeviceAuth(CommandDeviceId, CommandSiteCode, LegalEntityId);
        var response = await _client.PostAsJsonAsync(
            "/api/v1/agent/installations/android",
            new AndroidInstallationUpsertRequest
            {
                InstallationId = installationId,
                RegistrationToken = registrationToken,
                AppVersion = "1.0.0",
                OsVersion = "13",
                DeviceModel = "Urovo i9100"
            });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FccMiddlewareDbContext>();
        var ciphertext = await ReadCiphertextAsync(db, installationId);
        ciphertext.Should().StartWith("$aes256gcm$v1$");
        ciphertext.Should().NotBe(registrationToken);

        var audit = await db.AuditEvents.IgnoreQueryFilters()
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(item =>
                item.EventType == AgentControlAuditEventTypes.AgentInstallationUpdated
                && item.EntityId == CommandDeviceId);

        audit.Should().NotBeNull();
        audit!.Payload.Should().Contain(installationId.ToString());
        audit.Payload.Should().NotContain(registrationToken);
    }

    [Fact]
    public async Task CreateCommand_RejectsSensitivePayload()
    {
        SetPortalAuth("FccAdmin", "portal-admin", LegalEntityId);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/agents/{CommandDeviceId}/commands",
            new CreateAgentCommandRequest
            {
                CommandType = AgentCommandType.RESET_LOCAL_STATE,
                Reason = "Wipe local state after token exposure",
                Payload = JsonSerializer.SerializeToElement(new { registrationToken = "secret-token" })
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("errorCode").GetString().Should().Be("VALIDATION.SENSITIVE_PAYLOAD");
    }

    private void SetPortalAuth(string role, string oid, params Guid[] legalEntityIds)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(TestPortalSigningKey);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, oid),
            new("oid", oid),
            new("roles", role),
            new("legal_entities", string.Join(",", legalEntityIds))
        };

        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = TestPortalIssuer,
            Audience = TestPortalAudience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
        });

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenHandler.WriteToken(token));
    }

    private void SetDeviceAuth(Guid deviceId, string siteCode, Guid legalEntityId)
    {
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateDeviceJwt(deviceId, siteCode, legalEntityId));
    }

    private void ClearAuth() => _client.DefaultRequestHeaders.Authorization = null;

    private static string CreateDeviceJwt(Guid deviceId, string siteCode, Guid legalEntityId)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(TestDeviceSigningKey);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, deviceId.ToString()),
            new Claim("site", siteCode),
            new Claim("lei", legalEntityId.ToString())
        };

        var token = tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddHours(2),
            Issuer = TestDeviceIssuer,
            Audience = TestDeviceAudience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)
        });

        return tokenHandler.WriteToken(token);
    }

    private static async Task<string> ReadCiphertextAsync(FccMiddlewareDbContext db, Guid installationId)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "select registration_token_ciphertext from agent_installations where id = @id";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = installationId;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync();
            return result?.ToString() ?? string.Empty;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task SeedAsync(FccMiddlewareDbContext db)
    {
        var now = DateTimeOffset.UtcNow;

        db.LegalEntities.Add(new LegalEntity
        {
            Id = LegalEntityId,
            BusinessCode = "AO-001",
            CountryCode = "AO",
            CountryName = "Angola",
            Name = "Agent Control Test Entity",
            CurrencyCode = "AOA",
            TaxAuthorityCode = "AGT",
            FiscalizationRequired = false,
            DefaultTimezone = "Africa/Luanda",
            OdooCompanyId = "ODOO-AO-001",
            IsActive = true,
            SyncedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Sites.AddRange(
            new Site
            {
                Id = CommandSiteId,
                LegalEntityId = LegalEntityId,
                SiteCode = CommandSiteCode,
                SiteName = "Command Test Site",
                OperatingModel = SiteOperatingModel.COCO,
                CompanyTaxPayerId = "CMD-TAX-001",
                IsActive = true,
                SyncedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            },
            new Site
            {
                Id = RegistrationSiteId,
                LegalEntityId = LegalEntityId,
                SiteCode = RegistrationSiteCode,
                SiteName = "Registration Test Site",
                OperatingModel = SiteOperatingModel.COCO,
                CompanyTaxPayerId = "REG-TAX-001",
                IsActive = true,
                SyncedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            },
            new Site
            {
                Id = DecommissionSiteId,
                LegalEntityId = LegalEntityId,
                SiteCode = DecommissionSiteCode,
                SiteName = "Decommission Test Site",
                OperatingModel = SiteOperatingModel.COCO,
                CompanyTaxPayerId = "DEC-TAX-001",
                IsActive = true,
                SyncedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });

        db.FccConfigs.AddRange(
            CreateConfig(CommandConfigId, CommandSiteId, CommandSiteCode, 1, now),
            CreateConfig(RegistrationConfigId, RegistrationSiteId, RegistrationSiteCode, 1, now),
            CreateConfig(DecommissionConfigId, DecommissionSiteId, DecommissionSiteCode, 1, now));

        db.AgentRegistrations.AddRange(
            new AgentRegistration
            {
                Id = CommandDeviceId,
                SiteId = CommandSiteId,
                LegalEntityId = LegalEntityId,
                SiteCode = CommandSiteCode,
                DeviceSerialNumber = "CMD-DEVICE-001",
                DeviceModel = "Honeywell CT45",
                OsVersion = "13",
                AgentVersion = "2.0.0",
                IsActive = true,
                TokenHash = "command-device-token-hash",
                TokenExpiresAt = now.AddDays(30),
                RegisteredAt = now.AddHours(-2),
                LastSeenAt = now.AddMinutes(-2),
                CreatedAt = now.AddHours(-2),
                UpdatedAt = now.AddMinutes(-2)
            },
            new AgentRegistration
            {
                Id = DecommissionDeviceId,
                SiteId = DecommissionSiteId,
                LegalEntityId = LegalEntityId,
                SiteCode = DecommissionSiteCode,
                DeviceSerialNumber = "DEC-DEVICE-001",
                DeviceModel = "Honeywell CT45",
                OsVersion = "13",
                AgentVersion = "2.0.0",
                IsActive = true,
                TokenHash = "decommission-device-token-hash",
                TokenExpiresAt = now.AddDays(30),
                RegisteredAt = now.AddHours(-1),
                LastSeenAt = now.AddMinutes(-1),
                CreatedAt = now.AddHours(-1),
                UpdatedAt = now.AddMinutes(-1)
            });

        await db.SaveChangesAsync();
    }

    private static FccConfig CreateConfig(
        Guid configId,
        Guid siteId,
        string siteCode,
        int configVersion,
        DateTimeOffset now) =>
        new()
        {
            Id = configId,
            SiteId = siteId,
            LegalEntityId = LegalEntityId,
            FccVendor = FccVendor.DOMS,
            ConnectionProtocol = ConnectionProtocol.REST,
            HostAddress = "127.0.0.1",
            Port = 8080,
            CredentialRef = $"secret://{siteCode}",
            IngestionMethod = IngestionMethod.PULL,
            IngestionMode = IngestionMode.CLOUD_DIRECT,
            PullIntervalSeconds = 30,
            HeartbeatIntervalSeconds = 30,
            HeartbeatTimeoutSeconds = 180,
            IsActive = true,
            ConfigVersion = configVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
}
