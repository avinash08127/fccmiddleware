using System.Net;
using System.Text;
using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Advatec;

/// <summary>
/// Minimal HTTP listener for receiving Advatec Receipt webhook callbacks (ADV-3.1).
/// <para>
/// Listens on a configurable port for POST /api/webhook/advatec.
/// Validates the X-Webhook-Token header against the configured token.
/// Always returns 200 OK to Advatec (retry behaviour unknown — AQ-7) to avoid data loss.
/// Delivers received payloads to a callback delegate for processing.
/// </para>
/// </summary>
public sealed class AdvatecWebhookListener : IDisposable
{
    /// <summary>
    /// Delegate invoked when a valid webhook payload is received.
    /// The implementation should not throw; exceptions are caught and logged.
    /// </summary>
    /// <param name="payload">The raw webhook envelope ready for normalization.</param>
    /// <param name="ct">Cancellation token.</param>
    public delegate Task WebhookReceivedHandler(RawPayloadEnvelope payload, CancellationToken ct);

    private const string WebhookPath = "/api/webhook/advatec";
    private const string TokenHeader = "X-Webhook-Token";

    private readonly int _port;
    private readonly string _siteCode;
    private readonly string? _webhookToken;
    private readonly ILogger<AdvatecWebhookListener> _logger;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private WebhookReceivedHandler? _handler;

    /// <summary>
    /// Creates a new Advatec webhook listener.
    /// </summary>
    /// <param name="port">TCP port to listen on (e.g. 8091).</param>
    /// <param name="siteCode">Site code to stamp on received payloads.</param>
    /// <param name="webhookToken">
    /// Expected value of the X-Webhook-Token header. If null or empty, token validation is skipped.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public AdvatecWebhookListener(
        int port,
        string siteCode,
        string? webhookToken,
        ILogger<AdvatecWebhookListener> logger)
    {
        _port = port;
        _siteCode = siteCode;
        _webhookToken = webhookToken;
        _logger = logger;
    }

    /// <summary>
    /// Whether the listener is currently running and accepting connections.
    /// </summary>
    public bool IsListening => _listener?.IsListening ?? false;

    /// <summary>
    /// Starts the webhook listener on the configured port.
    /// </summary>
    /// <param name="handler">
    /// Callback invoked for each valid webhook payload. Must be set before calling Start.
    /// </param>
    /// <param name="ct">Cancellation token to stop the listener.</param>
    /// <exception cref="InvalidOperationException">Thrown if the listener is already running.</exception>
    public void Start(WebhookReceivedHandler handler, CancellationToken ct = default)
    {
        if (_listener is not null)
            throw new InvalidOperationException("Advatec webhook listener is already running");

        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_port}{WebhookPath}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex,
                "Advatec webhook listener failed to start on port {Port} (path: {Path}). "
                + "On Windows, you may need to grant a URL ACL for this port.",
                _port, WebhookPath);
            _listener = null;
            _cts.Dispose();
            _cts = null;
            throw;
        }

        _logger.LogInformation(
            "Advatec webhook listener started on port {Port} (path: {Path})",
            _port, WebhookPath);

        _listenTask = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Gracefully stops the webhook listener.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        if (_listener is not null)
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }

            _listener = null;
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Advatec webhook listener accept loop ended with error");
            }

            _listenTask = null;
        }

        _logger.LogInformation("Advatec webhook listener stopped");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        _cts?.Dispose();
        _listener = null;
        _cts = null;
    }

    // ── Private methods ──────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Advatec webhook listener: error accepting connection");
                continue;
            }

            // Process each request in the background so the accept loop stays responsive.
            _ = ProcessRequestAsync(context, ct);
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;

        try
        {
            var request = context.Request;

            // Only accept POST requests.
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Advatec webhook: rejected {Method} request (only POST accepted)", request.HttpMethod);
                await WriteResponse(response, 405, "Method Not Allowed");
                return;
            }

            // Validate X-Webhook-Token header.
            if (!string.IsNullOrEmpty(_webhookToken))
            {
                var providedToken = request.Headers[TokenHeader];

                // Also check query parameter ?token= as a fallback (Advatec may configure webhook URL with token param).
                if (string.IsNullOrEmpty(providedToken))
                {
                    providedToken = request.QueryString["token"];
                }

                if (string.IsNullOrEmpty(providedToken) ||
                    !string.Equals(providedToken, _webhookToken, StringComparison.Ordinal))
                {
                    _logger.LogWarning("Advatec webhook: rejected request with invalid or missing webhook token");
                    // Still return 200 to avoid leaking information about token validation.
                    await WriteResponse(response, 200, "OK");
                    return;
                }
            }

            // Read the request body.
            string body;
            using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(ct);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                _logger.LogWarning("Advatec webhook: received empty body");
                await WriteResponse(response, 200, "OK");
                return;
            }

            _logger.LogDebug("Advatec webhook: received payload ({Length} bytes)", body.Length);

            // Always return 200 OK immediately (Advatec retry behaviour unknown — AQ-7).
            await WriteResponse(response, 200, "OK");

            // Build the raw payload envelope and deliver to the handler.
            var envelope = new RawPayloadEnvelope(
                FccVendor: "Advatec",
                SiteCode: _siteCode,
                RawJson: body,
                ReceivedAt: DateTimeOffset.UtcNow);

            if (_handler is not null)
            {
                try
                {
                    await _handler(envelope, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Advatec webhook: handler failed for payload (body length={Length})", body.Length);
                    // Do not re-throw -- the 200 is already sent.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Advatec webhook: unhandled error processing request");

            // Best-effort 200 response to avoid potential retries.
            try
            {
                await WriteResponse(response, 200, "OK");
            }
            catch
            {
                // Response may already be sent or connection may be broken.
            }
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch
            {
                // Ignore close errors.
            }
        }
    }

    private static async Task WriteResponse(HttpListenerResponse response, int statusCode, string body)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";

        var buffer = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = buffer.Length;

        await response.OutputStream.WriteAsync(buffer);
        await response.OutputStream.FlushAsync();
    }
}
