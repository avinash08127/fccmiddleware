using FccMiddleware.Api.Infrastructure;
using FccMiddleware.Application.AgentConfig;
using FccMiddleware.Domain.Entities;
using FccMiddleware.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FccMiddleware.Api.Tests.Infrastructure;

public sealed class AuthoritativeWriteFenceServiceTests
{
    private static readonly Guid SiteId = Guid.Parse("71000000-0000-0000-0000-000000000001");
    private static readonly Guid LegalEntityId = Guid.Parse("71000000-0000-0000-0000-000000000002");
    private const string SiteCode = "SITE-HA-001";

    [Fact]
    public async Task ValidateAsync_WhenSiteHaDisabled_AllowsWrite()
    {
        var service = CreateService(enabled: false, []);

        var result = await service.ValidateAsync("not-a-guid", SiteCode, null, CancellationToken.None);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WhenLeaderEpochMissing_ReturnsValidationError()
    {
        var leader = CreateAgent(Guid.Parse("71000000-0000-0000-0000-000000000011"), "DESKTOP", priority: 10, leaderEpochSeen: 4);
        var service = CreateService(enabled: true, [leader]);

        var result = await service.ValidateAsync(leader.Id.ToString(), SiteCode, null, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.ErrorCode.Should().Be("VALIDATION.LEADER_EPOCH_REQUIRED");
    }

    [Fact]
    public async Task ValidateAsync_WhenLeaderEpochIsStale_ReturnsConflict()
    {
        var leader = CreateAgent(Guid.Parse("71000000-0000-0000-0000-000000000021"), "DESKTOP", priority: 10, leaderEpochSeen: 5);
        var standby = CreateAgent(Guid.Parse("71000000-0000-0000-0000-000000000022"), "ANDROID", priority: 20, leaderEpochSeen: 5);
        var service = CreateService(enabled: true, [leader, standby]);

        var result = await service.ValidateAsync(leader.Id.ToString(), SiteCode, 4, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        result.ErrorCode.Should().Be("CONFLICT.STALE_LEADER_EPOCH");
    }

    [Fact]
    public async Task ValidateAsync_WhenCurrentEpochMatchesButWriterIsStandby_ReturnsConflict()
    {
        var leader = CreateAgent(Guid.Parse("71000000-0000-0000-0000-000000000031"), "DESKTOP", priority: 10, leaderEpochSeen: 6);
        var standby = CreateAgent(Guid.Parse("71000000-0000-0000-0000-000000000032"), "ANDROID", priority: 20, leaderEpochSeen: 6);
        var service = CreateService(enabled: true, [leader, standby]);

        var result = await service.ValidateAsync(standby.Id.ToString(), SiteCode, 6, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        result.ErrorCode.Should().Be("CONFLICT.NON_LEADER_WRITE");
    }

    [Fact]
    public async Task ValidateAsync_WhenHigherEpochFromNewAgent_AllowsAndUpdatesLeader()
    {
        var previousLeader = CreateAgent(Guid.Parse("71000000-0000-0000-0000-000000000051"), "DESKTOP", priority: 10, leaderEpochSeen: 5);
        var newLeader = CreateAgent(Guid.Parse("71000000-0000-0000-0000-000000000052"), "ANDROID", priority: 5, leaderEpochSeen: 5);
        var service = CreateService(enabled: true, [previousLeader, newLeader]);

        // New agent presents epoch 6, higher than current max of 5
        var result = await service.ValidateAsync(newLeader.Id.ToString(), SiteCode, 6, CancellationToken.None);

        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WhenWriterIsCurrentLeaderWithCurrentEpoch_AllowsWrite()
    {
        var leader = CreateAgent(Guid.Parse("71000000-0000-0000-0000-000000000041"), "DESKTOP", priority: 10, leaderEpochSeen: 7);
        var standby = CreateAgent(Guid.Parse("71000000-0000-0000-0000-000000000042"), "ANDROID", priority: 20, leaderEpochSeen: 7);
        var service = CreateService(enabled: true, [leader, standby]);

        var result = await service.ValidateAsync(leader.Id.ToString(), SiteCode, 7, CancellationToken.None);

        result.IsAllowed.Should().BeTrue();
    }

    private static AuthoritativeWriteFenceService CreateService(bool enabled, IReadOnlyList<AgentRegistration> agents)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EdgeAgentDefaults:SiteHa:Enabled"] = enabled.ToString()
            })
            .Build();

        return new AuthoritativeWriteFenceService(
            new FakeAgentConfigDbContext(agents),
            configuration,
            NullLogger<AuthoritativeWriteFenceService>.Instance);
    }

    private static AgentRegistration CreateAgent(Guid id, string deviceClass, int priority, long leaderEpochSeen) =>
        new()
        {
            Id = id,
            SiteId = SiteId,
            LegalEntityId = LegalEntityId,
            SiteCode = SiteCode,
            DeviceSerialNumber = $"SER-{id:N}",
            DeviceModel = deviceClass,
            OsVersion = "test",
            AgentVersion = "1.0.0",
            DeviceClass = deviceClass,
            RoleCapability = "PRIMARY_ELIGIBLE",
            SiteHaPriority = priority,
            LeaderEpochSeen = leaderEpochSeen,
            Status = AgentRegistrationStatus.ACTIVE,
            IsActive = true,
            RegisteredAt = DateTimeOffset.Parse("2026-03-14T00:00:00Z").AddMinutes(priority),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private sealed class FakeAgentConfigDbContext : IAgentConfigDbContext
    {
        private readonly IReadOnlyList<AgentRegistration> _agents;

        public FakeAgentConfigDbContext(IReadOnlyList<AgentRegistration> agents)
        {
            _agents = agents;
        }

        public Task<FccConfig?> GetFccConfigWithSiteDataAsync(string siteCode, Guid legalEntityId, CancellationToken ct) =>
            Task.FromResult<FccConfig?>(null);

        public Task<AdapterDefaultConfig?> GetAdapterDefaultConfigAsync(Guid legalEntityId, string adapterKey, CancellationToken ct) =>
            Task.FromResult<AdapterDefaultConfig?>(null);

        public Task<SiteAdapterOverride?> GetSiteAdapterOverrideAsync(Guid siteId, string adapterKey, CancellationToken ct) =>
            Task.FromResult<SiteAdapterOverride?>(null);

        public Task<List<AgentRegistration>> GetSiteAgentsAsync(Guid siteId, CancellationToken ct) =>
            Task.FromResult(_agents.Where(agent => agent.SiteId == siteId).ToList());

        public Task<AgentRegistration?> FindAgentByDeviceIdAsync(Guid deviceId, CancellationToken ct) =>
            Task.FromResult(_agents.FirstOrDefault(agent => agent.Id == deviceId));
    }
}
