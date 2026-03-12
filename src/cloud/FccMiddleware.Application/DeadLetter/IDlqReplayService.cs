namespace FccMiddleware.Application.DeadLetter;

/// <summary>
/// Replays a dead-letter item through the original pipeline instead of just flipping status.
/// </summary>
public interface IDlqReplayService
{
    /// <summary>
    /// Attempts to replay the dead-letter item through the appropriate pipeline.
    /// Returns a result indicating success or failure with error details.
    /// </summary>
    Task<DlqReplayResult> ReplayAsync(Guid deadLetterId, CancellationToken cancellationToken = default);
}

public sealed record DlqReplayResult
{
    public required bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    /// <summary>The ID of the newly created transaction/record on successful replay.</summary>
    public Guid? ReplayedEntityId { get; init; }
}
