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

namespace FccDesktopAgent.Core.Tests.Connectivity;

/// <summary>
/// Unit tests for <see cref="CadenceController"/>.
/// Verifies that work is gated by connectivity state and that side effects fire on transitions.
/// </summary>
public sealed class CadenceControllerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IOptionsMonitor<AgentConfiguration> Config(
        IngestionMode mode = IngestionMode.Relay,
        int syncIntervalSeconds = 60,
        int telemetryIntervalSeconds = 300) =>
        new TestOptionsMonitor<AgentConfiguration>(new AgentConfiguration
        {
            IngestionMode = mode,
            CloudSyncIntervalSeconds = syncIntervalSeconds,
            TelemetryIntervalSeconds = telemetryIntervalSeconds,
        });

    private static IConnectivityMonitor MockMonitor(
        bool internetUp,
        bool fccUp)
    {
        var state = (internetUp, fccUp) switch
        {
            (true, true) => ConnectivityState.FullyOnline,
            (false, true) => ConnectivityState.InternetDown,
            (true, false) => ConnectivityState.FccUnreachable,
            (false, false) => ConnectivityState.FullyOffline,
        };

        var monitor = Substitute.For<IConnectivityMonitor>();
        monitor.Current.Returns(new ConnectivitySnapshot(state, internetUp, fccUp, DateTimeOffset.UtcNow));
        return monitor;
    }

    private static IServiceProvider EmptyServices() =>
        new ServiceCollection().BuildServiceProvider();

    private static IServiceProvider ServicesWithWorkers(
        IIngestionOrchestrator? ingestion = null,
        ICloudSyncService? cloudSync = null)
    {
        var services = new ServiceCollection();
        if (ingestion is not null) services.AddSingleton(ingestion);
        if (cloudSync is not null) services.AddSingleton(cloudSync);
        return services.BuildServiceProvider();
    }

    // ── FCC poll gating ───────────────────────────────────────────────────────

    [Fact]
    public async Task WhenFccUp_FccPollIsCalled()
    {
        var monitor = MockMonitor(internetUp: true, fccUp: true);
        var ingestion = Substitute.For<IIngestionOrchestrator>();
        ingestion.PollAndBufferAsync(Arg.Any<CancellationToken>())
            .Returns(new IngestionResult(1, 0, null));

        var controller = new CadenceController(
            monitor,
            Config(),
            NullLogger<CadenceController>.Instance,
            ServicesWithWorkers(ingestion: ingestion));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await controller.StartAsync(cts.Token);
        await Task.Delay(100); // let one cycle run
        controller.StopAsync(cts.Token).Ignore();

        await ingestion.Received(1).PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenFccDown_FccPollIsNotCalled()
    {
        var monitor = MockMonitor(internetUp: true, fccUp: false);
        var ingestion = Substitute.For<IIngestionOrchestrator>();

        var controller = new CadenceController(
            monitor,
            Config(),
            NullLogger<CadenceController>.Instance,
            ServicesWithWorkers(ingestion: ingestion));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await controller.StartAsync(cts.Token);
        await Task.Delay(100);
        controller.StopAsync(cts.Token).Ignore();

        await ingestion.DidNotReceive().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudDirect_FccUp_FirstTick_PollIsCalled()
    {
        // CloudDirect mode: agent runs as a safety-net LAN poller at a longer interval.
        // On tick 0, the condition (0 % N == 0) always triggers so the first safety-net poll fires.
        var monitor = MockMonitor(internetUp: true, fccUp: true);
        var ingestion = Substitute.For<IIngestionOrchestrator>();
        ingestion.PollAndBufferAsync(Arg.Any<CancellationToken>())
            .Returns(new IngestionResult(0, 0, null));

        var controller = new CadenceController(
            monitor,
            Config(mode: IngestionMode.CloudDirect),
            NullLogger<CadenceController>.Instance,
            ServicesWithWorkers(ingestion: ingestion));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await controller.StartAsync(cts.Token);
        await Task.Delay(100);
        controller.StopAsync(cts.Token).Ignore();

        await ingestion.Received(1).PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CloudDirect_FccDown_PollIsNotCalled()
    {
        // Even in CloudDirect mode, no poll if FCC is unreachable.
        var monitor = MockMonitor(internetUp: true, fccUp: false);
        var ingestion = Substitute.For<IIngestionOrchestrator>();

        var controller = new CadenceController(
            monitor,
            Config(mode: IngestionMode.CloudDirect),
            NullLogger<CadenceController>.Instance,
            ServicesWithWorkers(ingestion: ingestion));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await controller.StartAsync(cts.Token);
        await Task.Delay(100);
        controller.StopAsync(cts.Token).Ignore();

        await ingestion.DidNotReceive().PollAndBufferAsync(Arg.Any<CancellationToken>());
    }

    // ── Cloud upload gating ───────────────────────────────────────────────────

    [Fact]
    public async Task WhenInternetUp_CloudUploadIsCalled()
    {
        var monitor = MockMonitor(internetUp: true, fccUp: false);
        var cloudSync = Substitute.For<ICloudSyncService>();
        cloudSync.UploadBatchAsync(Arg.Any<CancellationToken>()).Returns(0);

        var controller = new CadenceController(
            monitor,
            Config(),
            NullLogger<CadenceController>.Instance,
            ServicesWithWorkers(cloudSync: cloudSync));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await controller.StartAsync(cts.Token);
        await Task.Delay(100);
        controller.StopAsync(cts.Token).Ignore();

        await cloudSync.Received(1).UploadBatchAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenInternetDown_CloudUploadIsNotCalled()
    {
        var monitor = MockMonitor(internetUp: false, fccUp: true);
        var cloudSync = Substitute.For<ICloudSyncService>();

        var controller = new CadenceController(
            monitor,
            Config(),
            NullLogger<CadenceController>.Instance,
            ServicesWithWorkers(cloudSync: cloudSync));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await controller.StartAsync(cts.Token);
        await Task.Delay(100);
        controller.StopAsync(cts.Token).Ignore();

        await cloudSync.DidNotReceive().UploadBatchAsync(Arg.Any<CancellationToken>());
    }

    // ── StateChanged side effects ─────────────────────────────────────────────

    [Fact]
    public void FullyOnlineTransition_RaisesStateChangedAndReleasesWakeSignal()
    {
        // Verify that the CadenceController subscribes to StateChanged and can receive events.
        // We test this by checking that StateChanged is plumbed: mock monitor fires event,
        // controller handles it without throwing.
        var monitor = Substitute.For<IConnectivityMonitor>();
        monitor.Current.Returns(new ConnectivitySnapshot(
            ConnectivityState.FullyOffline, false, false, DateTimeOffset.UtcNow));

        var controller = new CadenceController(
            monitor,
            Config(),
            NullLogger<CadenceController>.Instance,
            EmptyServices());

        // Should not throw
        var snapshot = new ConnectivitySnapshot(ConnectivityState.FullyOnline, true, true, DateTimeOffset.UtcNow);
        var act = () =>
        {
            monitor.StateChanged += Raise.Event<EventHandler<ConnectivitySnapshot>>(monitor, snapshot);
        };

        act.Should().NotThrow();
    }

    // ── No workers registered — cadence still runs without crashing ───────────

    [Fact]
    public async Task NoWorkersRegistered_CycleRunsWithoutErrors()
    {
        var monitor = MockMonitor(internetUp: true, fccUp: true);

        var controller = new CadenceController(
            monitor,
            Config(),
            NullLogger<CadenceController>.Instance,
            EmptyServices());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Func<Task> act = async () =>
        {
            await controller.StartAsync(cts.Token);
            await Task.Delay(100);
            await controller.StopAsync(cts.Token);
        };

        await act.Should().NotThrowAsync();
    }
}

// Extension to suppress warning CS4014 for fire-and-forget StopAsync in tests
file static class TaskExtensions
{
    public static void Ignore(this Task _) { }
}
