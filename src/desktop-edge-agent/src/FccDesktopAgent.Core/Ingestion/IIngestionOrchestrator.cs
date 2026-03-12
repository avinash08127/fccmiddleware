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
    /// Serialized with <see cref="ManualPullAsync"/> via an internal poll lock.
    /// </summary>
    Task<IngestionResult> PollAndBufferAsync(CancellationToken ct);

    /// <summary>
    /// On-demand FCC pull triggered by Odoo POS via the local REST API (DEA-2.7).
    /// Serialized with <see cref="PollAndBufferAsync"/> — manual and scheduled pulls never race.
    /// <paramref name="pumpNumber"/> is informational (logged for diagnostics); all transactions
    /// since the last cursor are fetched so no data is lost for other pumps.
    /// </summary>
    Task<IngestionResult> ManualPullAsync(int? pumpNumber, CancellationToken ct);
}

public sealed record IngestionResult(
    int NewTransactionsBuffered,
    int DuplicatesSkipped,
    string? LastFccSequence,
    int FetchCycles = 0);
