using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Radix;

/// <summary>
/// Radix unsolicited push listener (RX-5.1).
///
/// When the Radix FDC operates in UNSOLICITED mode (MODE=2), it pushes completed
/// transaction notifications to the desktop agent via HTTP POST instead of waiting
/// for the agent to poll. This listener:
///
/// <list type="bullet">
///   <item>Starts an <see cref="HttpListener"/> on a configurable port</item>
///   <item>Accepts POST requests containing <c>&lt;FDC_RESP&gt;</c> XML with RESP_CODE=30</item>
///   <item>Validates the USN-Code header and SHA-1 signature via <see cref="RadixSignatureHelper"/></item>
///   <item>Returns an XML ACK (<c>&lt;HOST_REQ&gt;</c> with CMD_CODE=201) to the FDC</item>
///   <item>Enqueues validated transaction XML into a <see cref="ConcurrentQueue{T}"/> for
///         the adapter to drain and normalize</item>
///   <item>Provides <see cref="StartAsync"/>/<see cref="StopAsync"/> lifecycle methods</item>
/// </list>
///
/// Thread safety: The incoming queue is a lock-free <see cref="ConcurrentQueue{T}"/>.
/// The listener runs on a background task. Signature validation prevents spoofed
/// transactions from rogue LAN devices.
/// </summary>
public sealed class RadixPushListener : IDisposable
{
    private readonly int _listenPort;
    private readonly int _expectedUsnCode;
    private readonly string _sharedSecret;
    private readonly ILogger _logger;

    private HttpListener? _httpListener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _isRunning;

    /// <summary>Thread-safe queue of raw XML payloads received from the FDC.</summary>
    private readonly ConcurrentQueue<PushedTransactionPayload> _incomingQueue = new();

    /// <summary>Atomic counter tracking queue size to avoid TOCTOU race on size check + enqueue.</summary>
    private int _queueCount;

    /// <summary>RESP_CODE for unsolicited push transactions from the FDC.</summary>
    public const int RespCodeUnsolicited = 30;

    /// <summary>Maximum queued transactions before dropping (back-pressure safety).</summary>
    public const int MaxQueueSize = 10_000;

    /// <summary>Whether the listener is currently accepting connections.</summary>
    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    /// <summary>Number of transactions currently queued and not yet drained by the adapter.</summary>
    public int QueueSize => _incomingQueue.Count;

    /// <summary>
    /// Creates a new push listener instance.
    /// </summary>
    /// <param name="listenPort">Port for the HTTP listener.</param>
    /// <param name="expectedUsnCode">Expected USN-Code header value from the FDC.</param>
    /// <param name="sharedSecret">Shared secret for SHA-1 signature validation.</param>
    /// <param name="logger">Logger instance.</param>
    public RadixPushListener(
        int listenPort,
        int expectedUsnCode,
        string sharedSecret,
        ILogger logger)
    {
        _listenPort = listenPort;
        _expectedUsnCode = expectedUsnCode;
        _sharedSecret = sharedSecret;
        _logger = logger;
    }

    /// <summary>
    /// Starts the HTTP listener on the configured port.
    ///
    /// Idempotent — calling Start on an already-running listener is a no-op.
    /// The listener binds to <c>http://+:{port}/</c> and accepts POSTs.
    /// </summary>
    /// <returns>true if the listener started (or was already running), false on bind failure.</returns>
    public async Task<bool> StartAsync()
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1) return true;

        try
        {
            _cts = new CancellationTokenSource();
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add($"http://+:{_listenPort}/");
            _httpListener.Start();

            _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);

            _logger.LogInformation("RadixPushListener started on port {Port}", _listenPort);
            return true;
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex,
                "RadixPushListener failed to bind http://+:{Port}/. " +
                "The FDC cannot reach a localhost-only listener — push mode will not work. " +
                "Ensure the URL ACL is configured: netsh http add urlacl url=http://+:{Port}/ user=Everyone",
                _listenPort, _listenPort);
            Interlocked.Exchange(ref _isRunning, 0);
            _cts?.Dispose();
            _cts = null;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RadixPushListener failed to start: {Message}", ex.Message);
            Interlocked.Exchange(ref _isRunning, 0);
            return false;
        }
    }

    /// <summary>
    /// Stops the HTTP listener and clears the incoming queue.
    ///
    /// Idempotent — calling Stop on an already-stopped listener is a no-op.
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _isRunning, 0) == 0) return;

        try
        {
            _cts?.Cancel();

            try
            {
                _httpListener?.Stop();
                _httpListener?.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed — ignore
            }

            if (_listenTask is not null)
            {
                try
                {
                    await _listenTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("RadixPushListener listen task did not complete within 2s");
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
            }

            _logger.LogInformation("RadixPushListener stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RadixPushListener error during shutdown: {Message}", ex.Message);
        }
        finally
        {
            _httpListener = null;
            _cts?.Dispose();
            _cts = null;
            _listenTask = null;

            // Clear queue
            while (_incomingQueue.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _queueCount, 0);
        }
    }

    /// <summary>
    /// Drains all queued pushed transactions (up to <paramref name="maxCount"/>) and returns them.
    ///
    /// Non-blocking. Returns an empty list if no transactions are available.
    /// The adapter calls this during its fetch cycle to collect pushed transactions.
    /// </summary>
    /// <param name="maxCount">Maximum number of transactions to drain in one call.</param>
    /// <returns>List of pushed transaction payloads, oldest first.</returns>
    public List<PushedTransactionPayload> DrainQueue(int maxCount = 200)
    {
        var result = new List<PushedTransactionPayload>(Math.Min(maxCount, _incomingQueue.Count));

        for (var i = 0; i < maxCount; i++)
        {
            if (!_incomingQueue.TryDequeue(out var item))
                break;

            Interlocked.Decrement(ref _queueCount);
            result.Add(item);
        }

        return result;
    }

    // =====================================================================
    // Private — Listen loop
    // =====================================================================

    /// <summary>
    /// Background loop that accepts incoming HTTP requests from the FDC.
    /// </summary>
    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _httpListener is { IsListening: true })
        {
            try
            {
                var context = await _httpListener.GetContextAsync().ConfigureAwait(false);

                // Handle each request on a separate task to avoid blocking the accept loop
                _ = Task.Run(() => HandleRequestAsync(context), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                // Listener stopped — expected
                break;
            }
            catch (ObjectDisposedException)
            {
                // Listener disposed — expected during shutdown
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "RadixPushListener accept error: {Message}", ex.Message);
                // Brief delay before retrying accept
                try { await Task.Delay(100, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    // =====================================================================
    // Private — Request handling
    // =====================================================================

    /// <summary>
    /// Handles an incoming HTTP request from the FDC.
    ///
    /// Validation steps:
    /// <list type="number">
    ///   <item>Verify HTTP method is POST</item>
    ///   <item>Read raw XML body</item>
    ///   <item>Validate USN-Code header matches expected value</item>
    ///   <item>Parse XML to extract RESP_CODE (must be 30 or 201 for transaction data)</item>
    ///   <item>Validate SHA-1 signature using <see cref="RadixSignatureHelper"/></item>
    ///   <item>Enqueue the raw XML for later normalization</item>
    ///   <item>Return XML ACK (CMD_CODE=201) to the FDC</item>
    /// </list>
    ///
    /// On any validation failure, returns HTTP 400 with an error XML body.
    /// </summary>
    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            // Step 1: Verify POST method
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await SendResponseAsync(response, HttpStatusCode.MethodNotAllowed,
                    BuildErrorResponse("Only POST is accepted")).ConfigureAwait(false);
                return;
            }

            // Step 2: Read raw XML body
            string rawXml;
            using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
            {
                rawXml = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(rawXml))
            {
                _logger.LogWarning("RadixPushListener: received empty body");
                await SendResponseAsync(response, HttpStatusCode.BadRequest,
                    BuildErrorResponse("Empty request body")).ConfigureAwait(false);
                return;
            }

            // Step 3: Validate USN-Code header
            var usnHeader = request.Headers["USN-Code"];
            if (!int.TryParse(usnHeader, out var usnValue) || usnValue != _expectedUsnCode)
            {
                _logger.LogWarning("RadixPushListener: USN-Code mismatch: expected={Expected}, received={Received}",
                    _expectedUsnCode, usnHeader);
                await SendResponseAsync(response, HttpStatusCode.BadRequest,
                    BuildErrorResponse("Invalid USN-Code")).ConfigureAwait(false);
                return;
            }

            // Step 4: Parse XML to verify it is a valid transaction
            var txnResp = RadixXmlParser.ParseTransactionResponse(rawXml);
            if (txnResp is null)
            {
                _logger.LogWarning("RadixPushListener: failed to parse pushed XML");
                await SendResponseAsync(response, HttpStatusCode.BadRequest,
                    BuildErrorResponse("Invalid XML")).ConfigureAwait(false);
                return;
            }

            if (txnResp.RespCode != RespCodeUnsolicited && txnResp.RespCode != RadixAdapter.RespCodeSuccess)
            {
                _logger.LogWarning("RadixPushListener: unexpected RESP_CODE={RespCode}", txnResp.RespCode);
                await SendResponseAsync(response, HttpStatusCode.BadRequest,
                    BuildErrorResponse("Unexpected RESP_CODE")).ConfigureAwait(false);
                return;
            }

            if (txnResp.Transaction is null)
            {
                _logger.LogWarning("RadixPushListener: no TRN element in pushed transaction (RESP_CODE={RespCode})",
                    txnResp.RespCode);
                await SendResponseAsync(response, HttpStatusCode.BadRequest,
                    BuildErrorResponse("Missing TRN element")).ConfigureAwait(false);
                return;
            }

            // Step 5: Validate SHA-1 signature
            if (!string.IsNullOrWhiteSpace(_sharedSecret))
            {
                var signatureValid = RadixXmlParser.ValidateTransactionResponseSignature(rawXml, _sharedSecret);
                if (!signatureValid)
                {
                    _logger.LogWarning("RadixPushListener: signature validation failed");
                    await SendResponseAsync(response, HttpStatusCode.Forbidden,
                        BuildErrorResponse("Signature validation failed")).ConfigureAwait(false);
                    return;
                }
            }

            // Step 6: Enqueue for adapter processing (with back-pressure guard).
            // Use atomic increment-then-check to avoid TOCTOU race between
            // concurrent push requests that could each pass a size check
            // before any of them enqueue.
            var newCount = Interlocked.Increment(ref _queueCount);
            if (newCount > MaxQueueSize)
            {
                Interlocked.Decrement(ref _queueCount);
                _logger.LogWarning("RadixPushListener: queue full ({MaxSize}) — dropping transaction", MaxQueueSize);
                await SendResponseAsync(response, HttpStatusCode.ServiceUnavailable,
                    BuildErrorResponse("Queue full")).ConfigureAwait(false);
                return;
            }

            var pushed = new PushedTransactionPayload(
                RawXml: rawXml,
                ReceivedAt: DateTimeOffset.UtcNow);

            _incomingQueue.Enqueue(pushed);
            _logger.LogDebug("RadixPushListener: enqueued pushed transaction (queue size={QueueSize})", newCount);

            // Step 7: Return XML ACK to FDC
            await SendResponseAsync(response, HttpStatusCode.OK, BuildAckResponse()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RadixPushListener: error handling push request: {Message}", ex.Message);

            try
            {
                await SendResponseAsync(context.Response, HttpStatusCode.InternalServerError,
                    BuildErrorResponse("Internal error")).ConfigureAwait(false);
            }
            catch
            {
                // Response already sent or connection closed — ignore
            }
        }
    }

    // =====================================================================
    // Private — HTTP response helpers
    // =====================================================================

    /// <summary>
    /// Sends an HTTP response with XML body.
    /// </summary>
    private static async Task SendResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, string body)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "Application/xml";
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bodyBytes.Length;
        await response.OutputStream.WriteAsync(bodyBytes).ConfigureAwait(false);
        response.Close();
    }

    // =====================================================================
    // Private — XML response builders
    // =====================================================================

    /// <summary>
    /// Builds the XML ACK response sent back to the FDC after a successful push.
    ///
    /// The FDC expects a HOST_REQ-style acknowledgment with CMD_CODE=201
    /// to confirm the transaction was received. Delegates to
    /// <see cref="RadixXmlBuilder.BuildTransactionAck"/> to include a proper
    /// SHA-1 signature, matching the pull-mode ACK format.
    /// </summary>
    private string BuildAckResponse()
    {
        return RadixXmlBuilder.BuildTransactionAck(_expectedUsnCode, token: 0, _sharedSecret);
    }

    /// <summary>
    /// Builds an error XML response for the FDC.
    /// </summary>
    private static string BuildErrorResponse(string message)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<HOST_REQ>\n");
        sb.Append("<REQ>\n");
        sb.Append("    <CMD_CODE>255</CMD_CODE>\n");
        sb.Append("    <CMD_NAME>ERROR</CMD_NAME>\n");
        sb.Append("    <MSG>").Append(EscapeXml(message)).Append("</MSG>\n");
        sb.Append("</REQ>\n");
        sb.Append("</HOST_REQ>");
        return sb.ToString();
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    // =====================================================================
    // IDisposable
    // =====================================================================

    /// <summary>
    /// Disposes the listener. Calls <see cref="StopAsync"/> synchronously.
    /// Prefer <see cref="StopAsync"/> for clean async shutdown.
    ///
    /// Uses Task.Run to avoid deadlocking when called from a thread with a
    /// SynchronizationContext (e.g., Avalonia UI thread).
    /// </summary>
    public void Dispose()
    {
        Task.Run(() => StopAsync()).GetAwaiter().GetResult();
    }
}

/// <summary>
/// A raw transaction payload pushed by the FDC in UNSOLICITED mode.
///
/// Stored in the <see cref="RadixPushListener"/>'s queue until drained by the adapter
/// for normalization.
/// </summary>
public sealed record PushedTransactionPayload(
    /// <summary>Complete raw XML body from the FDC POST request.</summary>
    string RawXml,
    /// <summary>When the push was received by the listener.</summary>
    DateTimeOffset ReceivedAt);
