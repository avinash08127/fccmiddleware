using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Doms.Jpl;

/// <summary>
/// Sends periodic JPL heartbeat frames and detects dead connections.
/// A dead connection is detected when no response is received within 3x the heartbeat interval.
/// Listens to <see cref="JplTcpClient.OnActivityReceived"/> to track last-seen activity.
/// </summary>
public sealed class JplHeartbeatManager : IAsyncDisposable
{
    private readonly JplTcpClient _client;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _deadThreshold;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _heartbeatTask;
    private DateTimeOffset _lastActivity;
    private volatile bool _disposed;

    /// <summary>Raised when no activity has been received for 3x the heartbeat interval.</summary>
    public event Action<string>? OnDeadConnection;

    /// <param name="client">The JPL TCP client to send heartbeats through.</param>
    /// <param name="intervalSeconds">Heartbeat interval in seconds (default 30).</param>
    /// <param name="logger">Logger instance.</param>
    public JplHeartbeatManager(JplTcpClient client, int intervalSeconds, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(intervalSeconds, 0);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _interval = TimeSpan.FromSeconds(intervalSeconds);
        _deadThreshold = _interval * 3;
        _logger = logger;
        _lastActivity = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Begin periodic heartbeat. Subscribes to the client's activity events
    /// and starts a background timer loop.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _lastActivity = DateTimeOffset.UtcNow;
        _client.OnActivityReceived += RecordActivity;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token), CancellationToken.None);

        _logger.LogInformation("JPL heartbeat started (interval={IntervalSeconds}s, dead threshold={ThresholdSeconds}s)",
            _interval.TotalSeconds, _deadThreshold.TotalSeconds);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the heartbeat timer. Unsubscribes from client events.
    /// Idempotent — safe to call when already stopped.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _client.OnActivityReceived -= RecordActivity;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("JPL heartbeat loop did not exit within 5 s during stop");
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _logger.LogInformation("JPL heartbeat stopped");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
        _cts?.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void RecordActivity()
    {
        _lastActivity = DateTimeOffset.UtcNow;
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Check for dead connection before sending.
            var elapsed = DateTimeOffset.UtcNow - _lastActivity;
            if (elapsed > _deadThreshold)
            {
                var reason = $"No activity for {elapsed.TotalSeconds:F0}s (threshold {_deadThreshold.TotalSeconds:F0}s)";
                _logger.LogWarning("JPL dead connection detected: {Reason}", reason);
                OnDeadConnection?.Invoke(reason);
                break;
            }

            if (!_client.IsConnected)
            {
                _logger.LogDebug("JPL heartbeat skipped — client not connected");
                continue;
            }

            try
            {
                await _client.SendHeartbeatAsync(ct);
                _logger.LogTrace("JPL heartbeat sent");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JPL heartbeat send failed");
            }
        }
    }
}
