namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Thread-safe rolling error counter using <see cref="Interlocked"/> operations.
/// Registered as a singleton so all components share one instance.
/// Counters are reset to zero after each successful telemetry submission.
/// </summary>
public sealed class ErrorCountTracker : IErrorCountTracker
{
    private int _fccConnectionErrors;
    private int _cloudUploadErrors;
    private int _cloudAuthErrors;
    private int _localApiErrors;
    private int _bufferWriteErrors;
    private int _adapterNormalizationErrors;
    private int _preAuthErrors;

    public void IncrementFccConnectionErrors() => Interlocked.Increment(ref _fccConnectionErrors);
    public void IncrementCloudUploadErrors() => Interlocked.Increment(ref _cloudUploadErrors);
    public void IncrementCloudAuthErrors() => Interlocked.Increment(ref _cloudAuthErrors);
    public void IncrementLocalApiErrors() => Interlocked.Increment(ref _localApiErrors);
    public void IncrementBufferWriteErrors() => Interlocked.Increment(ref _bufferWriteErrors);
    public void IncrementAdapterNormalizationErrors() => Interlocked.Increment(ref _adapterNormalizationErrors);
    public void IncrementPreAuthErrors() => Interlocked.Increment(ref _preAuthErrors);

    /// <inheritdoc />
    public ErrorCountSnapshot TakeSnapshot()
    {
        return new ErrorCountSnapshot(
            FccConnectionErrors: Interlocked.Exchange(ref _fccConnectionErrors, 0),
            CloudUploadErrors: Interlocked.Exchange(ref _cloudUploadErrors, 0),
            CloudAuthErrors: Interlocked.Exchange(ref _cloudAuthErrors, 0),
            LocalApiErrors: Interlocked.Exchange(ref _localApiErrors, 0),
            BufferWriteErrors: Interlocked.Exchange(ref _bufferWriteErrors, 0),
            AdapterNormalizationErrors: Interlocked.Exchange(ref _adapterNormalizationErrors, 0),
            PreAuthErrors: Interlocked.Exchange(ref _preAuthErrors, 0));
    }

    /// <inheritdoc />
    public ErrorCountSnapshot Peek()
    {
        return new ErrorCountSnapshot(
            FccConnectionErrors: Volatile.Read(ref _fccConnectionErrors),
            CloudUploadErrors: Volatile.Read(ref _cloudUploadErrors),
            CloudAuthErrors: Volatile.Read(ref _cloudAuthErrors),
            LocalApiErrors: Volatile.Read(ref _localApiErrors),
            BufferWriteErrors: Volatile.Read(ref _bufferWriteErrors),
            AdapterNormalizationErrors: Volatile.Read(ref _adapterNormalizationErrors),
            PreAuthErrors: Volatile.Read(ref _preAuthErrors));
    }
}
