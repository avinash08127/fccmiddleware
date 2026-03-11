using Microsoft.Extensions.Options;
using VirtualLab.Application.FccProfiles;
using VirtualLab.Domain.Benchmarking;
using VirtualLab.Domain.Enums;
using VirtualLab.Infrastructure.FccProfiles;
using VirtualLab.Infrastructure.Persistence.Seed;
using VirtualLab.Tests.Persistence;

namespace VirtualLab.Tests.FccProfiles;

public sealed class FccProfileServiceTests
{
    [Fact]
    public async Task ResolveBySiteCodeReturnsActiveProfileContract()
    {
        await using SqliteTestDb testDb = new();
        await SeedAsync(testDb);
        FccProfileService service = new(testDb.DbContext);

        ResolvedFccProfile? resolved = await service.ResolveBySiteCodeAsync("VL-MW-BT001");

        Assert.NotNull(resolved);
        Assert.Equal("doms-like", resolved.ProfileKey);
        Assert.Equal(PreAuthFlowMode.CreateThenAuthorize, resolved.PreAuthMode);
        Assert.Contains(resolved.Contract.EndpointSurface, x => x.Operation == "health");
        Assert.True(resolved.Contract.Capabilities.SupportsPush);
        Assert.True(resolved.Contract.Capabilities.SupportsPull);
    }

    [Fact]
    public async Task ValidateRejectsIncompleteProfile()
    {
        await using SqliteTestDb testDb = new();
        await SeedAsync(testDb);
        FccProfileService service = new(testDb.DbContext);

        FccProfileRecord draft = new()
        {
            LabEnvironmentId = testDb.DbContext.LabEnvironments.Single().Id,
            ProfileKey = "broken-profile",
            Name = "Broken Profile",
            VendorFamily = "GENERIC",
            DeliveryMode = TransactionDeliveryMode.Push,
            Contract = new()
            {
                PreAuthMode = PreAuthFlowMode.CreateThenAuthorize,
                Capabilities = new()
                {
                    SupportsPush = true,
                },
                Auth = new()
                {
                    Mode = SimulatedAuthMode.ApiKey,
                },
            },
        };

        FccProfileValidationResult result = await service.ValidateAsync(draft);

        Assert.False(result.IsValid);
        Assert.Contains(result.Messages, x => x.Path == "contract.endpointSurface");
        Assert.Contains(result.Messages, x => x.Path == "contract.auth.apiKeyHeaderName");
        Assert.Contains(result.Messages, x => x.Path == "contract.fieldMappings");
    }

    [Fact]
    public async Task PreviewRendersTemplateWithSampleValues()
    {
        await using SqliteTestDb testDb = new();
        await SeedAsync(testDb);
        FccProfileService service = new(testDb.DbContext);

        FccProfileSummary profile = (await service.ListAsync()).Single(x => x.ProfileKey == "doms-like");
        FccProfilePreviewResult preview = await service.PreviewAsync(
            new FccProfilePreviewRequest(
                profile.Id,
                null,
                "transactions-push",
                new Dictionary<string, string>
                {
                    ["siteCode"] = "VL-MW-BT001",
                    ["transactionId"] = "TX-9000",
                    ["amount"] = "4200",
                }));

        Assert.Contains("\"transactionId\":\"TX-9000\"", preview.RequestBody);
        Assert.Contains("\"accepted\":true", preview.ResponseBody);
        Assert.Equal("application/json", preview.RequestHeaders["content-type"]);
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
