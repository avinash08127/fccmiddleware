using FccDesktopAgent.Core.Peer;
using FccDesktopAgent.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Api.Auth;

/// <summary>
/// Endpoint filter that validates HMAC-SHA256 signatures on peer-to-peer API requests.
/// Applied to the /peer route group. Validates X-Peer-Signature and X-Peer-Timestamp headers
/// using <see cref="PeerHmacSigner.Verify"/> with the shared peer secret from the credential store.
/// </summary>
internal sealed class PeerHmacAuthFilter : IEndpointFilter
{
    private readonly ICredentialStore _credentialStore;
    private readonly ILogger<PeerHmacAuthFilter> _logger;

    public PeerHmacAuthFilter(
        ICredentialStore credentialStore,
        ILogger<PeerHmacAuthFilter> logger)
    {
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var request = httpContext.Request;

        // Read the shared secret from credential store
        var secret = await _credentialStore.GetSecretAsync(CredentialKeys.PeerSharedSecret);
        if (string.IsNullOrEmpty(secret))
        {
            // If no secret is configured, peer auth is effectively disabled — allow through
            _logger.LogDebug("Peer shared secret not configured — allowing unauthenticated peer request");
            return await next(context);
        }

        // Extract signature and timestamp headers
        if (!request.Headers.TryGetValue("X-Peer-Signature", out var signatureHeader) ||
            !request.Headers.TryGetValue("X-Peer-Timestamp", out var timestampHeader))
        {
            _logger.LogWarning("Peer request {Method} {Path} missing HMAC headers",
                request.Method, request.Path);
            return Results.Json(new
            {
                errorCode = "PEER_AUTH_MISSING",
                message = "Missing X-Peer-Signature or X-Peer-Timestamp header",
                timestamp = DateTimeOffset.UtcNow,
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var signature = signatureHeader.ToString();
        var timestamp = timestampHeader.ToString();

        // Read and buffer the request body for signature verification
        request.EnableBuffering();
        string? body = null;

        if (request.ContentLength is > 0)
        {
            using var reader = new StreamReader(request.Body, leaveOpen: true);
            body = await reader.ReadToEndAsync();
            request.Body.Position = 0; // Rewind for downstream handlers
        }

        var path = request.Path.Value ?? "/";
        var method = request.Method;

        if (!PeerHmacSigner.Verify(secret, method, path, timestamp, body, signature))
        {
            _logger.LogWarning("Peer request {Method} {Path} HMAC verification failed",
                method, path);
            return Results.Json(new
            {
                errorCode = "PEER_AUTH_INVALID",
                message = "Invalid peer HMAC signature or timestamp drift exceeded",
                timestamp = DateTimeOffset.UtcNow,
            }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}
