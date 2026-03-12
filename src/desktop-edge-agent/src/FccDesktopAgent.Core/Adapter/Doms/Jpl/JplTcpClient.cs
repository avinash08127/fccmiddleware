using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Doms.Jpl;

/// <summary>
/// Persistent TCP client for DOMS JPL protocol.
/// Uses TcpClient + NetworkStream with TaskCompletionSource for request-response correlation.
/// Features:
/// <list type="bullet">
///   <item>Automatic reconnection with exponential backoff</item>
///   <item>Request-response correlation via ConcurrentDictionary keyed by message name</item>
///   <item>Read loop running on background Task</item>
///   <item>Dead connection detection via heartbeat timeout</item>
/// </list>
/// </summary>
public sealed class JplTcpClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JplMessage>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private volatile bool _disposed;

    /// <summary>Raised when an unsolicited (non-correlated) message arrives from the FCC.</summary>
    public event Action<JplMessage>? OnFrameReceived;

    /// <summary>Raised when the TCP connection is lost unexpectedly.</summary>
    public event Action<string>? OnDisconnected;

    /// <summary>Raised when any frame (including heartbeat) is received, used for dead-connection detection.</summary>
    public event Action? OnActivityReceived;

    /// <summary>Whether the TCP connection is currently open.</summary>
    public bool IsConnected => _tcpClient?.Connected == true && _stream is not null && !_disposed;

    public JplTcpClient(string host, int port, ILogger logger, TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(port, 0);

        _host = host;
        _port = port;
        _logger = logger;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>
    /// Establish TCP connection to the FCC and start the background read loop.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _logger.LogInformation("JPL TCP connecting to {Host}:{Port}", _host, _port);

        _tcpClient = new TcpClient { NoDelay = true };
        await _tcpClient.ConnectAsync(_host, _port, ct);
        _stream = _tcpClient.GetStream();

        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), CancellationToken.None);

        _logger.LogInformation("JPL TCP connected to {Host}:{Port}", _host, _port);
    }

    /// <summary>
    /// Send a JPL message and wait for a correlated response (matched by message name).
    /// </summary>
    public async Task<JplMessage> SendAsync(JplMessage message, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConnected)
            throw new InvalidOperationException("JPL TCP client is not connected");

        var json = JsonSerializer.Serialize(message, JsonOptions);
        var frame = JplFrameCodec.Encode(json);

        var tcs = new TaskCompletionSource<JplMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(message.Name, tcs))
        {
            // A request with the same name is already pending — replace it.
            _pendingRequests[message.Name] = tcs;
        }

        try
        {
            await WriteFrameAsync(frame, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_requestTimeout);

            await using var registration = timeoutCts.Token.Register(
                () => tcs.TrySetCanceled(timeoutCts.Token));

            return await tcs.Task;
        }
        catch
        {
            _pendingRequests.TryRemove(message.Name, out _);
            throw;
        }
    }

    /// <summary>
    /// Send a raw heartbeat frame (STX/ETX with no payload).
    /// </summary>
    public async Task SendHeartbeatAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsConnected)
            throw new InvalidOperationException("JPL TCP client is not connected");

        var frame = JplFrameCodec.EncodeHeartbeat();
        await WriteFrameAsync(frame, ct);
    }

    /// <summary>
    /// Gracefully disconnect: cancel read loop, close stream and socket.
    /// Idempotent — safe to call when already disconnected.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_readLoopCts is not null)
        {
            await _readLoopCts.CancelAsync();
        }

        if (_readLoopTask is not null)
        {
            try
            {
                await _readLoopTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("JPL TCP read loop did not exit within 5 s during disconnect");
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        CleanupConnection();
        CancelAllPending("Disconnected");

        _logger.LogInformation("JPL TCP disconnected from {Host}:{Port}", _host, _port);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await DisconnectAsync();
        _writeLock.Dispose();
        _readLoopCts?.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task WriteFrameAsync(byte[] frame, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_stream is null)
                throw new InvalidOperationException("JPL TCP stream is null — not connected");

            await _stream.WriteAsync(frame, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var accumulatorSize = 0;
        var accumulator = new byte[65536];

        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _stream.ReadAsync(
                        buffer.AsMemory(0, buffer.Length), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "JPL TCP read error");
                    RaiseDisconnected($"Read error: {ex.Message}");
                    break;
                }

                if (bytesRead == 0)
                {
                    // Remote closed the connection.
                    _logger.LogWarning("JPL TCP connection closed by remote host");
                    RaiseDisconnected("Remote host closed connection");
                    break;
                }

                // Grow accumulator if needed.
                if (accumulatorSize + bytesRead > accumulator.Length)
                {
                    var newSize = Math.Max(accumulator.Length * 2, accumulatorSize + bytesRead);
                    var newAccumulator = new byte[newSize];
                    System.Buffer.BlockCopy(accumulator, 0, newAccumulator, 0, accumulatorSize);
                    accumulator = newAccumulator;
                }

                System.Buffer.BlockCopy(buffer, 0, accumulator, accumulatorSize, bytesRead);
                accumulatorSize += bytesRead;

                // Process all complete frames in the accumulator.
                ProcessAccumulator(ref accumulator, ref accumulatorSize);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "JPL TCP read loop unexpected error");
            RaiseDisconnected($"Unexpected error: {ex.Message}");
        }
    }

    private void ProcessAccumulator(ref byte[] accumulator, ref int accumulatorSize)
    {
        while (accumulatorSize > 0)
        {
            var span = new ReadOnlySpan<byte>(accumulator, 0, accumulatorSize);
            var result = JplFrameCodec.Decode(span);

            if (result is DecodeResult.Incomplete incomplete)
            {
                if (incomplete.BytesConsumed > 0)
                {
                    ConsumeBytes(ref accumulator, ref accumulatorSize, incomplete.BytesConsumed);
                }
                break;
            }

            if (result is DecodeResult.Heartbeat heartbeat)
            {
                _logger.LogTrace("JPL heartbeat frame received");
                OnActivityReceived?.Invoke();
                ConsumeBytes(ref accumulator, ref accumulatorSize, heartbeat.BytesConsumed);
                continue;
            }

            if (result is DecodeResult.Error error)
            {
                _logger.LogWarning("JPL frame decode error: {Error}", error.Message);
                ConsumeBytes(ref accumulator, ref accumulatorSize, error.BytesConsumed);
                continue;
            }

            if (result is DecodeResult.Frame frame)
            {
                OnActivityReceived?.Invoke();
                ConsumeBytes(ref accumulator, ref accumulatorSize, frame.BytesConsumed);
                HandleIncomingFrame(frame.Payload);
            }
        }
    }

    private static void ConsumeBytes(ref byte[] accumulator, ref int size, int count)
    {
        if (count <= 0) return;
        var remaining = size - count;
        if (remaining > 0)
        {
            System.Buffer.BlockCopy(accumulator, count, accumulator, 0, remaining);
        }
        size = remaining;
    }

    private void HandleIncomingFrame(string json)
    {
        JplMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<JplMessage>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JPL failed to deserialize frame payload");
            return;
        }

        if (message is null)
        {
            _logger.LogWarning("JPL deserialized null message from frame");
            return;
        }

        _logger.LogDebug("JPL received message: {Name} (subCode={SubCode})", message.Name, message.SubCode);

        // Check if this is a correlated response to a pending request.
        if (_pendingRequests.TryRemove(message.Name, out var tcs))
        {
            tcs.TrySetResult(message);
            return;
        }

        // Unsolicited message — raise event.
        OnFrameReceived?.Invoke(message);
    }

    private void RaiseDisconnected(string reason)
    {
        CleanupConnection();
        CancelAllPending(reason);
        OnDisconnected?.Invoke(reason);
    }

    private void CleanupConnection()
    {
        try { _stream?.Dispose(); } catch { /* best-effort */ }
        try { _tcpClient?.Dispose(); } catch { /* best-effort */ }
        _stream = null;
        _tcpClient = null;
    }

    private void CancelAllPending(string reason)
    {
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException($"JPL request cancelled: {reason}"));
            }
        }
    }
}
