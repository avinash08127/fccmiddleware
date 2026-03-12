using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VirtualLab.Domain.Benchmarking;
using VirtualLab.Domain.Enums;
using VirtualLab.Domain.Models;
using VirtualLab.Infrastructure.Persistence.Seed;

namespace VirtualLab.Tests.Persistence;

public sealed class VirtualLabPersistenceTests
{
    [Fact]
    public async Task MigrationAndSeedCreateQueryableForecourtAndPayloadHistory()
    {
        await using SqliteTestDb testDb = new();
        VirtualLabSeedService seedService = new(
            testDb.DbContext,
            Options.Create(new BenchmarkSeedProfile
            {
                Sites = 10,
                PumpsPerSite = 10,
                NozzlesPerPump = 4,
                Transactions = 10000,
                PushTransactions = 4000,
                PullTransactions = 3500,
                HybridTransactions = 2500,
                NormalOrderTransactions = 7000,
                PreAuthCreateOnlyTransactions = 1500,
                PreAuthCreateThenAuthorizeTransactions = 1500,
                CallbackTargets = 10,
                ScenarioSeed = 424242,
            }));

        await seedService.SeedAsync(resetExisting: false);

        Site? site = await testDb.DbContext.Sites
            .AsNoTracking()
            .Include(x => x.ActiveFccSimulatorProfile)
            .Include(x => x.Pumps)
                .ThenInclude(x => x.Nozzles)
                    .ThenInclude(x => x.Product)
            .SingleOrDefaultAsync(x => x.SiteCode == "VL-MW-BT001");

        Assert.NotNull(site);
        Assert.Equal(TransactionDeliveryMode.Hybrid, site.DeliveryMode);
        Assert.Contains("\"Operation\":\"health\"", site.ActiveFccSimulatorProfile.EndpointSurfaceJson);
        Assert.Contains("\"Mode\":1", site.ActiveFccSimulatorProfile.AuthConfigurationJson);
        Assert.Equal(2, site.Pumps.Count);
        Assert.Equal(6, site.Pumps.SelectMany(x => x.Nozzles).Count());
        Assert.Equal(3, site.Pumps.SelectMany(x => x.Nozzles).Select(x => x.ProductId).Distinct().Count());
        Assert.All(site.Pumps, pump => Assert.True(pump.LayoutX >= 0 && pump.LayoutY >= 0));

        SimulatedTransaction transaction = await testDb.DbContext.SimulatedTransactions
            .AsNoTracking()
            .SingleAsync();
        LabEventLog log = await testDb.DbContext.LabEventLogs
            .AsNoTracking()
            .OrderBy(x => x.OccurredAtUtc)
            .FirstAsync();
        CallbackAttempt callbackAttempt = await testDb.DbContext.CallbackAttempts
            .AsNoTracking()
            .SingleAsync();

        Assert.Contains("\"volume\":42.113", transaction.RawPayloadJson);
        Assert.Contains("\"amountMinorUnits\":972550", transaction.CanonicalPayloadJson);
        Assert.Equal("corr-default-flow", transaction.CorrelationId);
        Assert.Equal("TransactionGenerated", log.Category);
        Assert.Contains("\"seeded\":true", log.MetadataJson);
        Assert.Equal(1, callbackAttempt.AttemptNumber);
        Assert.Equal(202, callbackAttempt.ResponseStatusCode);
    }

    [Fact]
    public async Task RequiredIndexesArePresentForHotPaths()
    {
        await using SqliteTestDb testDb = new();

        string[] indexNames = await testDb.DbContext.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index'")
            .ToArrayAsync();

        Assert.Contains("IX_Sites_LabEnvironmentId_ActiveFccSimulatorProfileId", indexNames);
        Assert.Contains("IX_SimulatedTransactions_SiteId_Status_OccurredAtUtc", indexNames);
        Assert.Contains("IX_SimulatedTransactions_CorrelationId", indexNames);
        Assert.Contains("IX_LabEventLogs_SiteId_Category_OccurredAtUtc", indexNames);
        Assert.Contains("IX_CallbackAttempts_CallbackTargetId_AttemptedAtUtc", indexNames);
    }

    [Fact]
    public async Task LabEventLogConventionsNormalizeCategorySeverityAndPayloadDefaults()
    {
        await using SqliteTestDb testDb = new();

        testDb.DbContext.LabEventLogs.Add(new LabEventLog
        {
            Id = Guid.NewGuid(),
            Category = "authfailure",
            Severity = "warn",
            EventType = "AuthRejected",
            Message = "",
            RawPayloadJson = "",
            CanonicalPayloadJson = "",
            MetadataJson = "",
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });

        await testDb.DbContext.SaveChangesAsync();

        LabEventLog saved = await testDb.DbContext.LabEventLogs.SingleAsync();
        Assert.Equal("AuthFailure", saved.Category);
        Assert.Equal("Warning", saved.Severity);
        Assert.Equal("AuthRejected", saved.Message);
        Assert.Equal("{}", saved.RawPayloadJson);
        Assert.Equal("{}", saved.CanonicalPayloadJson);
        Assert.Equal("{}", saved.MetadataJson);
    }
}
