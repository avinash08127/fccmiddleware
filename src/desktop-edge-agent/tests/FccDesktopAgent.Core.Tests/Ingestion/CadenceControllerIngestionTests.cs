using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FccDesktopAgent.Core.Ingestion;
using FccDesktopAgent.Core.Runtime;
using FccDesktopAgent.Core.Sync;
using FccDesktopAgent.Core.Tests.Sync;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Ingestion;

/// <summary>
/// Tests that <see cref="CadenceController"/> correctly gates FCC polling based on
/// ingestion mode and connectivity state. Verifies the scheduling rules without
/// introducing independent timer loops (architecture rule #10).
/// </summary>
public sealed class CadenceControllerIngestionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IConnectivityMonitor MockMonitor(
        ConnectivityState state, bool internetUp, bool fccUp)
    {
        var monitor = Substitute.For<IConnectivityMonitor>();
        monitor.Current.Returns(new ConnectivitySnapshot(
            state, internetUp, fccUp, DateTimeOffset.UtcNow));
        return monitor;
    }

    private static (CadenceController, IIngestionOrchestrator) BuildController(
        IConnectivityMonitor monitor,
        AgentConfiguration config)
    {
        var orchestrator = Substitute.For<IIngestionOrchestrator>();
        orchestrator.PollAndBufferAsync(Arg.Any<CancellationToken>())
            .Returns(new IngestionResult(0, 0, null));

        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.UploadBatchAsync(Arg.Any<CancellationToken>())
            .Returns(0);

        var services = new ServiceCollection()
            .AddSingleton(orchestrator)
            .AddSingleton(cloudSync)
            .AddLogging()
            .BuildServiceProvider();

        var controller = new CadenceController(
            monitor,
            new TestOptionsMonitor<AgentConfiguration>(config),
            NullLogger<CadenceController>.Instance,
            services);

        return (controller, orchestrator);
    }

    /// <summary>
    /// Runs the cadence controller for one cycle (50ms), then stops it.
    /// Returns the orchestrator mock for assertion.
    /// </summary>
    private static async Task RunOneCycleAsync(CadenceController controller)
    {
        await controller.StartAsync(CancellationToken.None);
        await Task.Delay(50); // allow first cycle to complete; mocks return instantly
        await controller.StopAsync(CancellationToken.None);
    }

    // ── Relay mode ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RelayMode_FccUp_CallsOrchestrator()
    {
        var monitor = MockMonitor(ConnectivityState.FullyOnline, internetUp: true, fccUp: true);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.Relay,
            CloudSyncIntervalSeconds = 30,
        };

        var (controller, orchestrator) = BuildController(monitor, config);

        await RunOneCycleAsync(controller);

        await orchestrator.Received().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RelayMode_FccDown_SkipsOrchestrator()
    {
        var monitor = MockMonitor(ConnectivityState.FccUnreachable, internetUp: true, fccUp: false);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.Relay,
            CloudSyncIntervalSeconds = 30,
        };

        var (controller, orchestrator) = BuildController(monitor, config);

        await RunOneCycleAsync(controller);

        await orchestrator.DidNotReceive().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RelayMode_InternetDown_FccUp_StillCallsOrchestrator()
    {
        // Internet being down must NOT block FCC polling — buffer continues while internet is out.
        var monitor = MockMonitor(ConnectivityState.InternetDown, internetUp: false, fccUp: true);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.Relay,
            CloudSyncIntervalSeconds = 30,
        };

        var (controller, orchestrator) = BuildController(monitor, config);

        await RunOneCycleAsync(controller);

        await orchestrator.Received().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RelayMode_FullyOffline_SkipsOrchestrator()
    {
        var monitor = MockMonitor(ConnectivityState.FullyOffline, internetUp: false, fccUp: false);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.Relay,
            CloudSyncIntervalSeconds = 30,
        };

        var (controller, orchestrator) = BuildController(monitor, config);

        await RunOneCycleAsync(controller);

        await orchestrator.DidNotReceive().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    // ── BufferAlways mode ─────────────────────────────────────────────────────

    [Fact]
    public async Task BufferAlwaysMode_FccUp_CallsOrchestrator()
    {
        var monitor = MockMonitor(ConnectivityState.FullyOnline, internetUp: true, fccUp: true);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.BufferAlways,
            CloudSyncIntervalSeconds = 30,
        };

        var (controller, orchestrator) = BuildController(monitor, config);

        await RunOneCycleAsync(controller);

        await orchestrator.Received().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BufferAlwaysMode_FccDown_SkipsOrchestrator()
    {
        var monitor = MockMonitor(ConnectivityState.FccUnreachable, internetUp: true, fccUp: false);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.BufferAlways,
            CloudSyncIntervalSeconds = 30,
        };

        var (controller, orchestrator) = BuildController(monitor, config);

        await RunOneCycleAsync(controller);

        await orchestrator.DidNotReceive().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    // ── CloudDirect mode ──────────────────────────────────────────────────────

    [Fact]
    public async Task CloudDirectMode_FccUp_FirstTick_CallsOrchestrator()
    {
        // Tick 0: 0 % N == 0 for any N, so CloudDirect always polls on the first tick.
        var monitor = MockMonitor(ConnectivityState.FullyOnline, internetUp: true, fccUp: true);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.CloudDirect,
            CloudSyncIntervalSeconds = 30,
        };

        var (controller, orchestrator) = BuildController(monitor, config);

        await RunOneCycleAsync(controller);

        await orchestrator.Received().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudDirectMode_FccDown_SkipsOrchestrator()
    {
        var monitor = MockMonitor(ConnectivityState.FccUnreachable, internetUp: true, fccUp: false);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.CloudDirect,
            CloudSyncIntervalSeconds = 30,
        };

        var (controller, orchestrator) = BuildController(monitor, config);

        await RunOneCycleAsync(controller);

        await orchestrator.DidNotReceive().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    // ── Internet-down upload suppression ──────────────────────────────────────

    [Fact]
    public async Task AnyMode_InternetDown_SkipsCloudUpload()
    {
        var monitor = MockMonitor(ConnectivityState.InternetDown, internetUp: false, fccUp: true);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.Relay,
            CloudSyncIntervalSeconds = 30,
        };

        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.UploadBatchAsync(Arg.Any<CancellationToken>()).Returns(0);

        var orchestrator = Substitute.For<IIngestionOrchestrator>();
        orchestrator.PollAndBufferAsync(Arg.Any<CancellationToken>())
            .Returns(new IngestionResult(0, 0, null));

        var services = new ServiceCollection()
            .AddSingleton(orchestrator)
            .AddSingleton(cloudSync)
            .AddLogging()
            .BuildServiceProvider();

        var controller = new CadenceController(
            monitor,
            new TestOptionsMonitor<AgentConfiguration>(config),
            NullLogger<CadenceController>.Instance,
            services);

        await RunOneCycleAsync(controller);

        await cloudSync.DidNotReceive().UploadBatchAsync(Arg.Any<CancellationToken>());
        // But FCC polling still runs
        await orchestrator.Received().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FullyOnline_RunsBothFccPollAndCloudUpload()
    {
        var monitor = MockMonitor(ConnectivityState.FullyOnline, internetUp: true, fccUp: true);
        var config = new AgentConfiguration
        {
            IngestionMode = IngestionMode.Relay,
            CloudSyncIntervalSeconds = 30,
        };

        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.UploadBatchAsync(Arg.Any<CancellationToken>()).Returns(0);

        var orchestrator = Substitute.For<IIngestionOrchestrator>();
        orchestrator.PollAndBufferAsync(Arg.Any<CancellationToken>())
            .Returns(new IngestionResult(0, 0, null));

        var services = new ServiceCollection()
            .AddSingleton(orchestrator)
            .AddSingleton(cloudSync)
            .AddLogging()
            .BuildServiceProvider();

        var controller = new CadenceController(
            monitor,
            new TestOptionsMonitor<AgentConfiguration>(config),
            NullLogger<CadenceController>.Instance,
            services);

        await RunOneCycleAsync(controller);

        await orchestrator.Received().PollAndBufferAsync(Arg.Any<CancellationToken>());
        await cloudSync.Received().UploadBatchAsync(Arg.Any<CancellationToken>());
    }
}
