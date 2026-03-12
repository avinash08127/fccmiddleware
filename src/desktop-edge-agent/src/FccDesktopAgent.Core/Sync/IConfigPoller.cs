namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Polls cloud for configuration updates via <c>GET /api/v1/agent/config</c>.
/// Called by <see cref="Runtime.CadenceController"/> on internet-up ticks
/// (architecture rule #10: no independent timer loop).
/// </summary>
public interface IConfigPoller
{
    /// <summary>
    /// Poll for config updates. Returns <c>true</c> if new config was applied.
    /// </summary>
    Task<bool> PollAsync(CancellationToken ct);
}
