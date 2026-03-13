using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Connectivity;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Connectivity;

/// <summary>
/// Unit tests for <see cref="ConnectivityManager"/>.
/// Uses the internal test constructor to inject probe delegates, avoiding real I/O.
/// </summary>
public sealed class ConnectivityManagerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IOptionsMonitor<AgentConfiguration> DefaultConfig() =>
        new StaticOptionsMonitor(new AgentConfiguration
        {
            ConnectivityProbeIntervalSeconds = 30,
            CloudBaseUrl = "http://cloud.test.local",
        });

    private static ConnectivityManager Create(
        Func<CancellationToken, Task<bool>> internetProbe,
        Func<CancellationToken, Task<bool>> fccProbe) =>
        new(internetProbe, fccProbe, DefaultConfig(), NullLogger<ConnectivityManager>.Instance);

    private sealed class StaticOptionsMonitor(AgentConfiguration value) : IOptionsMonitor<AgentConfiguration>
    {
        public AgentConfiguration CurrentValue => value;
        public AgentConfiguration Get(string? name) => value;
        public IDisposable? OnChange(Action<AgentConfiguration, string?> listener) => null;
    }

    private static Task<bool> Up => Task.FromResult(true);
    private static Task<bool> Down => Task.FromResult(false);

    // ── Startup state ─────────────────────────────────────────────────────────

    [Fact]
    public void InitializesInFullyOfflineState()
    {
        var mgr = Create(_ => Up, _ => Up);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOffline);
        mgr.Current.IsInternetUp.Should().BeFalse();
        mgr.Current.IsFccUp.Should().BeFalse();
    }

    // ── State derivation from probe combinations ──────────────────────────────

    [Theory]
    [InlineData(true, true, ConnectivityState.FullyOnline)]
    [InlineData(false, true, ConnectivityState.InternetDown)]
    [InlineData(true, false, ConnectivityState.FccUnreachable)]
    [InlineData(false, false, ConnectivityState.FullyOffline)]
    public async Task StateDerivation_CorrectForAllCombinations(
        bool internetUp, bool fccUp, ConnectivityState expected)
    {
        var mgr = Create(_ => Task.FromResult(internetUp), _ => Task.FromResult(fccUp));

        // Both probes start DOWN. A true probe result recovers immediately.
        // A false result only goes DOWN after DownThreshold; since we're already DOWN
        // the initial state just stays DOWN for false probes.
        // Run DownThreshold cycles to ensure false probes have confirmed DOWN.
        for (int i = 0; i < ConnectivityManager.DownThreshold; i++)
            await mgr.RunSingleCycleAsync(CancellationToken.None);

        mgr.Current.State.Should().Be(expected);
        mgr.Current.IsInternetUp.Should().Be(internetUp);
        mgr.Current.IsFccUp.Should().Be(fccUp);
    }

    // ── UP recovery: single success transitions immediately ───────────────────

    [Fact]
    public async Task SingleSuccess_RecoversFccImmediately()
    {
        // Sequence: both UP, then FCC fails 3 times (→ DOWN), then FCC succeeds once (→ UP immediately).
        var internetResults = new Queue<bool>(new[] { true, true, true, true, true });
        var fccResults = new Queue<bool>(new[] { true, false, false, false, true });

        var mgr = Create(
            _ => Task.FromResult(internetResults.Dequeue()),
            _ => Task.FromResult(fccResults.Dequeue()));

        // Cycle 1: both UP → FullyOnline
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline);

        // Cycle 2: FCC failure #1 — still FullyOnline (below threshold)
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline, "1 FCC failure is below DownThreshold=3");

        // Cycle 3: FCC failure #2 — still FullyOnline
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline, "2 FCC failures is below DownThreshold=3");

        // Cycle 4: FCC failure #3 → FccUnreachable
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FccUnreachable, "3 consecutive failures triggers DOWN");

        // Cycle 5: single FCC success → immediately FullyOnline (no wait for consecutive successes)
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline, "single success recovers immediately");
        mgr.Current.IsFccUp.Should().BeTrue();
    }

    // ── DOWN detection: requires DownThreshold consecutive failures ───────────

    [Fact]
    public async Task DownThreshold_RequiresConsecutiveFailuresBeforeTransitioningDown()
    {
        // Start with both UP
        var internetResults = new Queue<bool>();
        var fccResults = new Queue<bool>();

        // Seed initial UP for both probes
        internetResults.Enqueue(true);
        fccResults.Enqueue(true);

        // Then 2 internet failures (below threshold)
        internetResults.Enqueue(false);
        fccResults.Enqueue(true);
        internetResults.Enqueue(false);
        fccResults.Enqueue(true);

        var mgr = Create(
            _ => Task.FromResult(internetResults.Dequeue()),
            _ => Task.FromResult(fccResults.Dequeue()));

        // Cycle 1: both UP → FullyOnline
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline);

        // Cycle 2: internet failure #1 → still FullyOnline (1 < DownThreshold=3)
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline,
            "one internet failure should NOT trigger InternetDown");

        // Cycle 3: internet failure #2 → still FullyOnline (2 < DownThreshold=3)
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline,
            "two internet failures should NOT trigger InternetDown");
    }

    [Fact]
    public async Task DownThreshold_TransitionsDownAfterThreeConsecutiveFailures()
    {
        var internetResults = new Queue<bool>(new[] { true, false, false, false });
        var fccResults = new Queue<bool>(new[] { true, true, true, true });

        var mgr = Create(
            _ => Task.FromResult(internetResults.Dequeue()),
            _ => Task.FromResult(fccResults.Dequeue()));

        // Cycle 1: both UP → FullyOnline
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline);

        // Cycles 2-3: failures accumulate but stay online
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline);

        // Cycle 4: third consecutive failure → InternetDown
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.InternetDown);
    }

    // ── Rapid alternation does not cause flapping ─────────────────────────────

    [Fact]
    public async Task RapidAlternation_DoesNotCauseFlapping()
    {
        // Start UP, then alternate down/up/down/up...
        // Each UP resets failure counter → never reaches DownThreshold.
        var internetResults = new Queue<bool>(new[] { true, false, true, false, true, false, true });
        var fccResults = Enumerable.Repeat(true, 7);

        var mgr = Create(
            _ => Task.FromResult(internetResults.Dequeue()),
            _ => Task.FromResult(fccResults.First()));

        await mgr.RunSingleCycleAsync(CancellationToken.None); // UP
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline);

        // Alternating cycles: should stay FullyOnline because failures are always interrupted
        for (int i = 0; i < 6; i++)
        {
            await mgr.RunSingleCycleAsync(CancellationToken.None);
        }

        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline,
            "alternating up/down should not cause flapping because failures reset on each success");
        mgr.Current.IsInternetUp.Should().BeTrue("last result was UP");
    }

    // ── StateChanged event fires on transitions only ──────────────────────────

    [Fact]
    public async Task StateChanged_FiresOnTransition()
    {
        var mgr = Create(_ => Up, _ => Up);

        var events = new List<ConnectivitySnapshot>();
        mgr.StateChanged += (_, snapshot) => events.Add(snapshot);

        // Cycle 1: FullyOffline → FullyOnline (should fire)
        await mgr.RunSingleCycleAsync(CancellationToken.None);

        events.Should().HaveCount(1);
        events[0].State.Should().Be(ConnectivityState.FullyOnline);
    }

    [Fact]
    public async Task StateChanged_DoesNotFireWhenStateUnchanged()
    {
        var mgr = Create(_ => Up, _ => Up);

        var events = new List<ConnectivitySnapshot>();
        mgr.StateChanged += (_, snapshot) => events.Add(snapshot);

        // First cycle: transitions from FullyOffline → FullyOnline
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        events.Should().HaveCount(1);

        // Second cycle: stays FullyOnline — no new event
        await mgr.RunSingleCycleAsync(CancellationToken.None);
        events.Should().HaveCount(1, "no state change means no event");
    }

    [Fact]
    public async Task StateChanged_PublishesCorrectSnapshotFields()
    {
        var mgr = Create(_ => Up, _ => Down);

        ConnectivitySnapshot? received = null;
        mgr.StateChanged += (_, s) => received = s;

        // FCC starts DOWN, internet UP → after first UP result for internet, state = FccUnreachable
        await mgr.RunSingleCycleAsync(CancellationToken.None);

        received.Should().NotBeNull();
        received!.State.Should().Be(ConnectivityState.FccUnreachable);
        received.IsInternetUp.Should().BeTrue();
        received.IsFccUp.Should().BeFalse();
        received.MeasuredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    // ── Current snapshot is always readable ──────────────────────────────────

    [Fact]
    public async Task Current_AlwaysReflectsLatestState()
    {
        var mgr = Create(_ => Up, _ => Up);

        mgr.Current.State.Should().Be(ConnectivityState.FullyOffline, "before any probe");

        await mgr.RunSingleCycleAsync(CancellationToken.None);
        mgr.Current.State.Should().Be(ConnectivityState.FullyOnline, "after successful probes");
    }

    // ── Multiple transitions fire multiple events ─────────────────────────────

    [Fact]
    public async Task MultipleTransitions_EachFiresEvent()
    {
        // FullyOffline → FullyOnline → InternetDown
        var internetResults = new Queue<bool>(new[] { true, false, false, false });
        var fccResults = new Queue<bool>(new[] { true, true, true, true });

        var mgr = Create(
            _ => Task.FromResult(internetResults.Dequeue()),
            _ => Task.FromResult(fccResults.Dequeue()));

        var states = new List<ConnectivityState>();
        mgr.StateChanged += (_, s) => states.Add(s.State);

        await mgr.RunSingleCycleAsync(CancellationToken.None); // → FullyOnline
        await mgr.RunSingleCycleAsync(CancellationToken.None); // failure 1 (no transition)
        await mgr.RunSingleCycleAsync(CancellationToken.None); // failure 2 (no transition)
        await mgr.RunSingleCycleAsync(CancellationToken.None); // failure 3 → InternetDown

        states.Should().BeEquivalentTo(
            new[] { ConnectivityState.FullyOnline, ConnectivityState.InternetDown },
            opts => opts.WithStrictOrdering());
    }
}
