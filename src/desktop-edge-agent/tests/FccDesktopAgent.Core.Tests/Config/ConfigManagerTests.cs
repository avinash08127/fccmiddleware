using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Config;

/// <summary>
/// Unit tests for <see cref="ConfigManager"/>.
/// Uses real in-memory SQLite for config storage and SyncState tracking.
/// </summary>
public sealed class ConfigManagerTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AgentDbContext> _dbOptions;
    private readonly AgentDbContext _db;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConfigManager _manager;

    public ConfigManagerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<AgentDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AgentDbContext(_dbOptions);
        _db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddScoped<AgentDbContext>(_ => new AgentDbContext(_dbOptions));
        services.AddLogging();
        _serviceProvider = services.BuildServiceProvider();

        _manager = new ConfigManager(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ConfigManager>.Instance);
    }

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SiteConfig MakeSiteConfig(
        int version = 1,
        DateTimeOffset? effectiveAt = null,
        SiteConfigSync? sync = null,
        SiteConfigTelemetry? telemetry = null,
        SiteConfigFcc? fcc = null,
        SiteConfigRollout? rollout = null)
    {
        var baseConfig = TestSiteConfigFactory.Create(version, effectiveAt);
        return baseConfig with
        {
            Sync = sync ?? baseConfig.Sync,
            Telemetry = telemetry ?? baseConfig.Telemetry,
            Fcc = fcc ?? baseConfig.Fcc,
            Rollout = rollout ?? baseConfig.Rollout,
        };
    }

    private static string ToJson(SiteConfig config) =>
        JsonSerializer.Serialize(config, CamelCase);

    // ── First config apply ────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyConfigAsync_FirstConfig_AppliedAndStored()
    {
        var config = MakeSiteConfig(version: 1);
        var json = ToJson(config);

        var result = await _manager.ApplyConfigAsync(config, json, "1", CancellationToken.None);

        result.Outcome.Should().Be(ConfigApplyOutcome.Applied);
        result.ConfigVersion.Should().Be(1);
        _manager.CurrentSiteConfig.Should().NotBeNull();
        _manager.CurrentConfigVersion.Should().Be("1");

        // Verify stored in database
        var record = await _db.AgentConfigs.FindAsync(1);
        record.Should().NotBeNull();
        record!.ConfigVersion.Should().Be("1");
        record.ConfigJson.Should().Be(json);
        record.AppliedAt.Should().NotBeNull();

        // Verify SyncState updated
        var syncState = await _db.SyncStates.FindAsync(1);
        syncState.Should().NotBeNull();
        syncState!.ConfigVersion.Should().Be("1");
        syncState.LastConfigSyncAt.Should().NotBeNull();
    }

    // ── Stale version rejected ────────────────────────────────────────────────

    [Fact]
    public async Task ApplyConfigAsync_StaleVersion_Rejected()
    {
        var config1 = MakeSiteConfig(version: 5);
        await _manager.ApplyConfigAsync(config1, ToJson(config1), "5", CancellationToken.None);

        var config2 = MakeSiteConfig(version: 3);
        var result = await _manager.ApplyConfigAsync(config2, ToJson(config2), "3", CancellationToken.None);

        result.Outcome.Should().Be(ConfigApplyOutcome.StaleVersion);
        _manager.CurrentSiteConfig!.ConfigVersion.Should().Be(5, "original config should be retained");
    }

    [Fact]
    public async Task ApplyConfigAsync_EqualVersion_Rejected()
    {
        var config1 = MakeSiteConfig(version: 5);
        await _manager.ApplyConfigAsync(config1, ToJson(config1), "5", CancellationToken.None);

        var config2 = MakeSiteConfig(version: 5);
        var result = await _manager.ApplyConfigAsync(config2, ToJson(config2), "5", CancellationToken.None);

        result.Outcome.Should().Be(ConfigApplyOutcome.StaleVersion);
    }

    // ── Not yet effective ─────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyConfigAsync_NotYetEffective_Deferred()
    {
        var config = MakeSiteConfig(
            version: 1,
            effectiveAt: DateTimeOffset.UtcNow.AddHours(1));

        var result = await _manager.ApplyConfigAsync(config, ToJson(config), "1", CancellationToken.None);

        result.Outcome.Should().Be(ConfigApplyOutcome.NotYetEffective);
        _manager.CurrentSiteConfig.Should().BeNull("config should not be applied when not yet effective");
    }

    [Fact]
    public async Task ApplyConfigAsync_UnsupportedVendor_Rejected()
    {
        var config = MakeSiteConfig(
            version: 1,
            fcc: TestSiteConfigFactory.Create().Fcc with
            {
                Vendor = "Advatec",
                HostAddress = "192.168.1.100",
                Port = 8080,
            });

        var result = await _manager.ApplyConfigAsync(config, ToJson(config), "1", CancellationToken.None);

        result.Outcome.Should().Be(ConfigApplyOutcome.Rejected);
        _manager.CurrentSiteConfig.Should().BeNull();
    }

    // ── Hot-reload fields applied ─────────────────────────────────────────────

    [Fact]
    public async Task PostConfigure_OverlaysCloudValues()
    {
        var siteConfig = MakeSiteConfig(
            sync: TestSiteConfigFactory.Create().Sync with
            {
                UploadIntervalSeconds = 120,
                UploadBatchSize = 100,
                ConfigPollIntervalSeconds = 90,
            },
            telemetry: TestSiteConfigFactory.Create().Telemetry with { TelemetryIntervalSeconds = 600 },
            fcc: TestSiteConfigFactory.Create().Fcc with
            {
                Vendor = "Radix",
                HostAddress = "192.168.1.100",
                Port = 8080,
                PullIntervalSeconds = 15,
            });

        // Apply config so PostConfigure has values to overlay
        await _manager.ApplyConfigAsync(siteConfig, ToJson(siteConfig), "1", CancellationToken.None);

        var options = new AgentConfiguration();
        _manager.PostConfigure(null, options);

        options.CloudSyncIntervalSeconds.Should().Be(120);
        options.UploadBatchSize.Should().Be(100);
        options.ConfigPollIntervalSeconds.Should().Be(90);
        options.TelemetryIntervalSeconds.Should().Be(600);
        options.FccPollIntervalSeconds.Should().Be(15);
        options.FccVendor.Should().Be(FccVendor.Radix);
        options.FccBaseUrl.Should().Be("http://192.168.1.100:8080");
    }

    [Fact]
    public void PostConfigure_NullConfig_NoChange()
    {
        var options = new AgentConfiguration
        {
            CloudSyncIntervalSeconds = 60,
            UploadBatchSize = 50,
        };

        // No config applied yet — PostConfigure should be a no-op
        _manager.PostConfigure(null, options);

        options.CloudSyncIntervalSeconds.Should().Be(60);
        options.UploadBatchSize.Should().Be(50);
    }

    // ── ConfigChanged event ───────────────────────────────────────────────────

    [Fact]
    public async Task ApplyConfigAsync_RaisesConfigChangedEvent()
    {
        ConfigChangedEventArgs? eventArgs = null;
        _manager.ConfigChanged += (_, e) => eventArgs = e;

        var config = MakeSiteConfig(version: 1);
        await _manager.ApplyConfigAsync(config, ToJson(config), "1", CancellationToken.None);

        eventArgs.Should().NotBeNull();
        eventArgs!.ConfigVersion.Should().Be(1);
    }

    // ── Restart-required sections ─────────────────────────────────────────────

    [Fact]
    public async Task ApplyConfigAsync_RestartRequiredSection_FlagsRestart()
    {
        var config1 = MakeSiteConfig(
            version: 1,
            fcc: TestSiteConfigFactory.Create().Fcc with { HostAddress = "192.168.1.100", Port = 8080 });
        await _manager.ApplyConfigAsync(config1, ToJson(config1), "1", CancellationToken.None);

        var config2 = MakeSiteConfig(
            version: 2,
            fcc: TestSiteConfigFactory.Create().Fcc with { HostAddress = "192.168.1.200", Port = 9090 },
            rollout: TestSiteConfigFactory.Create().Rollout with { RequiresRestartSections = ["fcc"] });

        var result = await _manager.ApplyConfigAsync(config2, ToJson(config2), "2", CancellationToken.None);

        result.Outcome.Should().Be(ConfigApplyOutcome.Applied);
        result.RestartRequiredSections.Should().Contain("fcc");
        _manager.RestartRequired.Should().BeTrue();
    }

    [Fact]
    public async Task ApplyConfigAsync_HotReloadableChange_NoRestartFlag()
    {
        var config1 = MakeSiteConfig(
            version: 1,
            telemetry: TestSiteConfigFactory.Create().Telemetry with { TelemetryIntervalSeconds = 300 });
        await _manager.ApplyConfigAsync(config1, ToJson(config1), "1", CancellationToken.None);

        var config2 = MakeSiteConfig(
            version: 2,
            telemetry: TestSiteConfigFactory.Create().Telemetry with { TelemetryIntervalSeconds = 600 });

        var result = await _manager.ApplyConfigAsync(config2, ToJson(config2), "2", CancellationToken.None);

        result.Outcome.Should().Be(ConfigApplyOutcome.Applied);
        result.HotReloadedSections.Should().Contain("telemetry");
        result.RestartRequiredSections.Should().BeEmpty();
        _manager.RestartRequired.Should().BeFalse();
    }

    // ── Load from database ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadFromDatabaseAsync_RestoresConfigFromDb()
    {
        // Seed a config record directly into the DB
        var config = MakeSiteConfig(version: 7);
        var json = ToJson(config);
        _db.AgentConfigs.Add(new AgentConfigRecord
        {
            Id = 1,
            ConfigJson = json,
            ConfigVersion = "7",
            AppliedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync();

        await _manager.LoadFromDatabaseAsync(CancellationToken.None);

        _manager.CurrentConfigVersion.Should().Be("7");
        _manager.CurrentSiteConfig.Should().NotBeNull();
        _manager.CurrentSiteConfig!.ConfigVersion.Should().Be(7);
    }

    [Fact]
    public async Task LoadFromDatabaseAsync_EmptyDb_NoError()
    {
        await _manager.LoadFromDatabaseAsync(CancellationToken.None);

        _manager.CurrentSiteConfig.Should().BeNull();
        _manager.CurrentConfigVersion.Should().BeNull();
    }

    // ── IOptionsChangeTokenSource ─────────────────────────────────────────────

    [Fact]
    public async Task ApplyConfigAsync_SignalsOptionsChangeToken()
    {
        var token = _manager.GetChangeToken();
        token.HasChanged.Should().BeFalse();

        var config = MakeSiteConfig(version: 1);
        await _manager.ApplyConfigAsync(config, ToJson(config), "1", CancellationToken.None);

        token.HasChanged.Should().BeTrue("options change token should fire when config is applied");
    }

    // ── Config version tracking in SyncState ──────────────────────────────────

    [Fact]
    public async Task ApplyConfigAsync_UpdatesSyncStateConfigVersion()
    {
        var config = MakeSiteConfig(version: 42);
        await _manager.ApplyConfigAsync(config, ToJson(config), "42", CancellationToken.None);

        var syncState = await _db.SyncStates.FindAsync(1);
        syncState.Should().NotBeNull();
        syncState!.ConfigVersion.Should().Be("42");
        syncState.LastConfigSyncAt.Should().NotBeNull();
    }

    // ── ApplyHotReloadFields static method ────────────────────────────────────

    [Fact]
    public void ApplyHotReloadFields_SetsIngestionMode()
    {
        var target = new AgentConfiguration();
        var source = TestSiteConfigFactory.Create() with
        {
            Fcc = TestSiteConfigFactory.Create().Fcc with { IngestionMode = "BUFFER_ALWAYS" },
        };

        ConfigManager.ApplyHotReloadFields(target, source);

        target.IngestionMode.Should().Be(FccDesktopAgent.Core.Adapter.Common.IngestionMode.BufferAlways);
    }

    [Fact]
    public void ApplyHotReloadFields_SetsBufferRetention()
    {
        var target = new AgentConfiguration();
        var source = TestSiteConfigFactory.Create() with
        {
            Buffer = TestSiteConfigFactory.Create().Buffer with { RetentionDays = 14, CleanupIntervalHours = 48 },
        };

        ConfigManager.ApplyHotReloadFields(target, source);

        target.RetentionDays.Should().Be(14);
        target.CleanupIntervalHours.Should().Be(48);
    }

    [Fact]
    public void ApplyHotReloadFields_MapsAllSiteHaFields()
    {
        var target = new AgentConfiguration();
        var source = TestSiteConfigFactory.Create() with
        {
            SiteHa = TestSiteConfigFactory.Create().SiteHa with
            {
                Enabled = true,
                AutoFailoverEnabled = true,
                Priority = 50,
                RoleCapability = "STANDBY_ONLY",
                CurrentRole = "STANDBY_HOT",
                HeartbeatIntervalSeconds = 3,
                FailoverTimeoutSeconds = 20,
                MaxReplicationLagSeconds = 10,
                ReplicationEnabled = true,
                ProxyingEnabled = false,
                LeaderEpoch = 42,
                PeerApiPort = 9090,
            },
        };

        ConfigManager.ApplyHotReloadFields(target, source);

        target.SiteHaEnabled.Should().BeTrue();
        target.AutoFailoverEnabled.Should().BeTrue();
        target.SiteHaPriority.Should().Be(50);
        target.RoleCapability.Should().Be("STANDBY_ONLY");
        target.CurrentRole.Should().Be("STANDBY_HOT");
        target.HeartbeatIntervalSeconds.Should().Be(3);
        target.FailoverTimeoutSeconds.Should().Be(20);
        target.MaxReplicationLagSeconds.Should().Be(10);
        target.ReplicationEnabled.Should().BeTrue();
        target.ProxyingEnabled.Should().BeFalse();
        target.LeaderEpoch.Should().Be(42);
        target.PeerApiPort.Should().Be(9090);
    }

    [Fact]
    public void ApplyHotReloadFields_NullSiteHa_LeavesDefaults()
    {
        var target = new AgentConfiguration
        {
            SiteHaEnabled = false,
            SiteHaPriority = 100,
            LeaderEpoch = 0,
        };
        var source = TestSiteConfigFactory.Create() with { SiteHa = null! };

        ConfigManager.ApplyHotReloadFields(target, source);

        target.SiteHaEnabled.Should().BeFalse();
        target.SiteHaPriority.Should().Be(100);
        target.LeaderEpoch.Should().Be(0);
    }
}
