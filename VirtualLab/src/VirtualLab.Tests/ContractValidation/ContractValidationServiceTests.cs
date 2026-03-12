using Microsoft.Extensions.Options;
using VirtualLab.Application.ContractValidation;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Domain.Benchmarking;
using VirtualLab.Infrastructure.ContractValidation;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Persistence.Seed;
using VirtualLab.Tests.Persistence;

namespace VirtualLab.Tests.ContractValidation;

public sealed class ContractValidationServiceTests
{
    [Fact]
    public async Task TransactionValidationPassesForSeededDomsLikePayloads()
    {
        await using SqliteTestDb testDb = new();
        await SeedAsync(testDb);
        FccProfileService profileService = new(testDb.DbContext);
        ContractValidationService validator = new();

        ResolvedFccProfile? profile = await profileService.ResolveBySiteCodeAsync("VL-MW-BT001");
        Assert.NotNull(profile);

        PayloadContractValidationReport report = validator.Validate(
            profile.Contract,
            ContractValidationScopes.Transaction,
            """{"transactionId":"TX-1001","correlationId":"corr-1001","siteCode":"VL-MW-BT001","pumpNumber":1,"nozzleNumber":1,"productCode":"PMS","volume":42.113,"amount":9725.50,"unitPrice":231.00,"currencyCode":"MWK","occurredAtUtc":"2026-03-11T00:00:04Z"}""",
            """{"fccTransactionId":"TX-1001","correlationId":"corr-1001","siteCode":"VL-MW-BT001","pumpNumber":1,"nozzleNumber":1,"productCode":"PMS","volumeMicrolitres":42113000,"amountMinorUnits":972550,"unitPriceMinorPerLitre":23100,"currencyCode":"MWK","startedAt":"2026-03-11T00:00:00Z","completedAt":"2026-03-11T00:00:04Z","fccVendor":"DOMS","status":"PENDING","schemaVersion":1}""");

        Assert.True(report.Enabled);
        Assert.Equal("Passed", report.Outcome);
        Assert.Empty(report.Issues);
        Assert.Contains(report.Comparisons, comparison => comparison.SourceField == "$.volume" && comparison.Status == "Matched");
    }

    [Fact]
    public async Task PreAuthRequestValidationFlagsMissingCanonicalCoverage()
    {
        await using SqliteTestDb testDb = new();
        await SeedAsync(testDb);
        FccProfileService profileService = new(testDb.DbContext);
        ContractValidationService validator = new();

        ResolvedFccProfile? profile = await profileService.ResolveBySiteCodeAsync("VL-MW-BT001");
        Assert.NotNull(profile);

        PayloadContractValidationReport report = validator.Validate(
            profile.Contract,
            ContractValidationScopes.PreAuthRequest,
            """{"correlationId":"corr-preauth-001","pump":1,"nozzle":1,"amount":15000}""",
            """{"siteCode":"VL-MW-BT001","preAuthId":"PA-001","correlationId":"corr-preauth-001","pumpNumber":1,"nozzleNumber":1,"requestedAmountMinorUnits":1500000,"currencyCode":"MWK","status":"PENDING","requestedAt":"2026-03-11T00:00:00Z","expiresAt":"2026-03-11T00:05:00Z"}""");

        Assert.True(report.Enabled);
        Assert.Equal("Failed", report.Outcome);
        Assert.Contains(report.Issues, issue => issue.Path == "$.productCode");
        Assert.Contains(report.Comparisons, comparison => comparison.TargetField == "$.requestedAmountMinorUnits" && comparison.Status == "Matched");
    }

    private static async Task SeedAsync(SqliteTestDb testDb)
    {
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
    }
}
