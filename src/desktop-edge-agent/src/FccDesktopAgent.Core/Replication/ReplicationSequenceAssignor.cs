namespace FccDesktopAgent.Core.Replication;

/// <summary>
/// Thread-safe monotonic counter for replication sequence numbers.
/// Initialized from the MAX(ReplicationSeq) in the database at startup.
/// </summary>
public sealed class ReplicationSequenceAssignor
{
    private long _currentSequence;

    /// <summary>
    /// Initialize the counter with the current maximum sequence from the database.
    /// Must be called once at startup before any calls to <see cref="NextSequence"/>.
    /// </summary>
    public void Initialize(long currentMax)
    {
        Interlocked.Exchange(ref _currentSequence, currentMax);
    }

    /// <summary>
    /// Returns the next monotonically increasing sequence number. Thread-safe.
    /// </summary>
    public long NextSequence()
    {
        return Interlocked.Increment(ref _currentSequence);
    }

    /// <summary>
    /// Returns the current (last assigned) sequence number without incrementing.
    /// </summary>
    public long CurrentSequence => Interlocked.Read(ref _currentSequence);
}
