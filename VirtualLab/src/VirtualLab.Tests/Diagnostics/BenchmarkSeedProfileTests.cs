using System.Text.Json;
using Microsoft.Extensions.Options;
using VirtualLab.Application.Diagnostics;
using VirtualLab.Domain.Benchmarking;

namespace VirtualLab.Tests.Diagnostics;

public sealed class BenchmarkSeedProfileTests
{
    [Fact]
    public void SeedProfileComputesStableReplaySignature()
    {
        string json = """
        {
          "profileName": "phase0-default",
          "sites": 10,
          "pumpsPerSite": 10,
          "nozzlesPerPump": 4,
          "transactions": 10000,
          "pushTransactions": 4000,
          "pullTransactions": 3500,
          "hybridTransactions": 2500,
          "normalOrderTransactions": 7000,
          "preAuthCreateOnlyTransactions": 1500,
          "preAuthCreateThenAuthorizeTransactions": 1500,
          "callbackTargets": 10,
          "scenarioSeed": 424242,
          "replayIterations": 3
        }
        """;

        BenchmarkSeedProfile seedProfile = JsonSerializer.Deserialize<BenchmarkSeedProfile>(json)!;

        string first = seedProfile.ComputeReplaySignature();
        string second = seedProfile.ComputeReplaySignature();

        Assert.Equal(first, second);
    }

    [Fact]
    public void DiagnosticProbeUsesGuardrailThresholds()
    {
        BenchmarkSeedProfile seedProfile = new()
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
        };

        DiagnosticProbeService service = new(Options.Create(seedProfile));
        DiagnosticProbeResult result = service.Run(5);

        Assert.Equal(VirtualLabGuardrails.DashboardLoadP95Ms, result.Thresholds.DashboardLoadP95Ms);
        Assert.Equal(VirtualLabGuardrails.FccEmulatorP95Ms, result.Thresholds.FccEmulatorP95Ms);
        Assert.Equal(VirtualLabGuardrails.TransactionPullP95Ms, result.Thresholds.TransactionPullP95Ms);
        Assert.NotEmpty(result.ReplaySignature);
    }
}
