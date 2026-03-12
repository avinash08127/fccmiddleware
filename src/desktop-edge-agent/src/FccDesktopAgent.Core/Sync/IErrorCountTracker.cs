namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Thread-safe rolling error counter. Incremented by various system components as errors occur.
/// The telemetry reporter calls <see cref="TakeSnapshot"/> to read and reset all counters
/// atomically after a successful telemetry submission.
/// </summary>
public interface IErrorCountTracker
{
    void IncrementFccConnectionErrors();
    void IncrementCloudUploadErrors();
    void IncrementCloudAuthErrors();
    void IncrementLocalApiErrors();
    void IncrementBufferWriteErrors();
    void IncrementAdapterNormalizationErrors();
    void IncrementPreAuthErrors();

    /// <summary>
    /// Returns the current error counts and resets all counters to zero.
    /// Thread-safe: uses <see cref="System.Threading.Interlocked.Exchange"/>.
    /// </summary>
    ErrorCountSnapshot TakeSnapshot();

    /// <summary>
    /// Returns the current error counts without resetting.
    /// Used to peek at values before deciding whether to reset.
    /// </summary>
    ErrorCountSnapshot Peek();
}

/// <summary>Immutable snapshot of all error counters at a point in time.</summary>
public sealed record ErrorCountSnapshot(
    int FccConnectionErrors,
    int CloudUploadErrors,
    int CloudAuthErrors,
    int LocalApiErrors,
    int BufferWriteErrors,
    int AdapterNormalizationErrors,
    int PreAuthErrors);
