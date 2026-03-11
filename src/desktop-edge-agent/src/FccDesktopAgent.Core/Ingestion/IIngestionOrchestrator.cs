namespace FccDesktopAgent.Core.Ingestion;

/// <summary>
/// Orchestrates FCC polling and local buffer ingestion.
/// Triggered by the cadence controller on each poll tick.
/// </summary>
public interface IIngestionOrchestrator
{
    /// <summary>
    /// Poll the FCC for new transactions and write them to the local buffer.
    /// Idempotent — duplicate FCC transaction IDs are ignored.
    /// </summary>
    Task<IngestionResult> PollAndBufferAsync(CancellationToken ct);
}

public sealed record IngestionResult(
    int NewTransactionsBuffered,
    int DuplicatesSkipped,
    string? LastFccSequence);
