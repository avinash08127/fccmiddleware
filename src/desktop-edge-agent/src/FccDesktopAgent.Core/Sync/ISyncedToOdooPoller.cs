namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Polls the cloud backend for transactions confirmed as SYNCED_TO_ODOO and
/// advances those records in the local SQLite buffer.
///
/// Called by <see cref="Runtime.CadenceController"/> on internet-up ticks
/// (architecture rule #10: no independent timer loop).
/// </summary>
public interface ISyncedToOdooPoller
{
    /// <summary>
    /// Polls cloud for SYNCED_TO_ODOO status updates and transitions matching
    /// local buffer records from Uploaded → SyncedToOdoo.
    /// Returns the number of records transitioned.
    /// </summary>
    Task<int> PollAsync(CancellationToken ct);
}
