using FccDesktopAgent.Core.Sync;
using FluentAssertions;
using Xunit;

namespace FccDesktopAgent.Core.Tests.Sync;

/// <summary>
/// Unit tests for <see cref="ErrorCountTracker"/>.
/// Verifies thread-safe increment, peek (non-destructive read), and take-snapshot (read + reset).
/// </summary>
public sealed class ErrorCountTrackerTests
{
    [Fact]
    public void AllCounters_StartAtZero()
    {
        var tracker = new ErrorCountTracker();
        var snapshot = tracker.Peek();

        snapshot.FccConnectionErrors.Should().Be(0);
        snapshot.CloudUploadErrors.Should().Be(0);
        snapshot.CloudAuthErrors.Should().Be(0);
        snapshot.LocalApiErrors.Should().Be(0);
        snapshot.BufferWriteErrors.Should().Be(0);
        snapshot.AdapterNormalizationErrors.Should().Be(0);
        snapshot.PreAuthErrors.Should().Be(0);
    }

    [Fact]
    public void Increment_IncreasesCorrespondingCounter()
    {
        var tracker = new ErrorCountTracker();

        tracker.IncrementFccConnectionErrors();
        tracker.IncrementFccConnectionErrors();
        tracker.IncrementCloudUploadErrors();
        tracker.IncrementCloudAuthErrors();
        tracker.IncrementLocalApiErrors();
        tracker.IncrementBufferWriteErrors();
        tracker.IncrementAdapterNormalizationErrors();
        tracker.IncrementPreAuthErrors();
        tracker.IncrementPreAuthErrors();
        tracker.IncrementPreAuthErrors();

        var snapshot = tracker.Peek();

        snapshot.FccConnectionErrors.Should().Be(2);
        snapshot.CloudUploadErrors.Should().Be(1);
        snapshot.CloudAuthErrors.Should().Be(1);
        snapshot.LocalApiErrors.Should().Be(1);
        snapshot.BufferWriteErrors.Should().Be(1);
        snapshot.AdapterNormalizationErrors.Should().Be(1);
        snapshot.PreAuthErrors.Should().Be(3);
    }

    [Fact]
    public void Peek_DoesNotResetCounters()
    {
        var tracker = new ErrorCountTracker();
        tracker.IncrementFccConnectionErrors();
        tracker.IncrementCloudUploadErrors();

        var first = tracker.Peek();
        var second = tracker.Peek();

        first.FccConnectionErrors.Should().Be(1);
        second.FccConnectionErrors.Should().Be(1);
        first.CloudUploadErrors.Should().Be(1);
        second.CloudUploadErrors.Should().Be(1);
    }

    [Fact]
    public void TakeSnapshot_ResetsAllCountersToZero()
    {
        var tracker = new ErrorCountTracker();
        tracker.IncrementFccConnectionErrors();
        tracker.IncrementCloudUploadErrors();
        tracker.IncrementCloudAuthErrors();
        tracker.IncrementLocalApiErrors();
        tracker.IncrementBufferWriteErrors();
        tracker.IncrementAdapterNormalizationErrors();
        tracker.IncrementPreAuthErrors();

        var snapshot = tracker.TakeSnapshot();

        snapshot.FccConnectionErrors.Should().Be(1);
        snapshot.CloudUploadErrors.Should().Be(1);
        snapshot.CloudAuthErrors.Should().Be(1);
        snapshot.LocalApiErrors.Should().Be(1);
        snapshot.BufferWriteErrors.Should().Be(1);
        snapshot.AdapterNormalizationErrors.Should().Be(1);
        snapshot.PreAuthErrors.Should().Be(1);

        // All counters should be zero after TakeSnapshot
        var afterReset = tracker.Peek();
        afterReset.FccConnectionErrors.Should().Be(0);
        afterReset.CloudUploadErrors.Should().Be(0);
        afterReset.CloudAuthErrors.Should().Be(0);
        afterReset.LocalApiErrors.Should().Be(0);
        afterReset.BufferWriteErrors.Should().Be(0);
        afterReset.AdapterNormalizationErrors.Should().Be(0);
        afterReset.PreAuthErrors.Should().Be(0);
    }

    [Fact]
    public void TakeSnapshot_FollowedByIncrement_TracksNewErrors()
    {
        var tracker = new ErrorCountTracker();
        tracker.IncrementFccConnectionErrors();
        tracker.IncrementFccConnectionErrors();

        // Reset
        tracker.TakeSnapshot();

        // Increment after reset
        tracker.IncrementFccConnectionErrors();

        var snapshot = tracker.Peek();
        snapshot.FccConnectionErrors.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentIncrements_AreThreadSafe()
    {
        var tracker = new ErrorCountTracker();
        const int incrementsPerThread = 1000;
        const int threadCount = 4;

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < incrementsPerThread; i++)
                tracker.IncrementFccConnectionErrors();
        })).ToArray();

        await Task.WhenAll(tasks);

        var snapshot = tracker.Peek();
        snapshot.FccConnectionErrors.Should().Be(incrementsPerThread * threadCount);
    }
}
