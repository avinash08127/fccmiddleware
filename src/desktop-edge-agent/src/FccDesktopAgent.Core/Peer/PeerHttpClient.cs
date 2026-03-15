using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Replication;
using FccDesktopAgent.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Peer;

/// <summary>
/// HTTP client for peer-to-peer communication in the HA cluster.
/// All requests are signed with HMAC-SHA256 using the shared peer secret.
/// </summary>
public interface IPeerHttpClient
{
    /// <summary>Send a heartbeat to a peer and return its response.</summary>
    Task<PeerHeartbeatResponse?> SendHeartbeatAsync(string baseUrl, PeerHeartbeatRequest request, CancellationToken ct);

    /// <summary>GET /peer/health on a peer.</summary>
    Task<PeerHealthResponse?> GetHealthAsync(string baseUrl, CancellationToken ct);

    /// <summary>POST /peer/claim-leadership to a peer.</summary>
    Task<PeerLeadershipClaimResponse?> ClaimLeadershipAsync(string baseUrl, PeerLeadershipClaimRequest request, CancellationToken ct);

    /// <summary>GET /peer/bootstrap — full snapshot from primary.</summary>
    Task<SnapshotPayload?> GetBootstrapAsync(string baseUrl, CancellationToken ct);

    /// <summary>GET /peer/sync?sinceSeq={seq}&amp;limit={limit} — delta sync from primary.</summary>
    Task<DeltaSyncPayload?> GetDeltaSyncAsync(string baseUrl, long sinceSeq, int limit, CancellationToken ct);

    /// <summary>POST /peer/proxy/preauth — forward pre-auth to primary.</summary>
    Task<PeerProxyPreAuthResponse?> ProxyPreAuthAsync(string baseUrl, PeerProxyPreAuthRequest request, CancellationToken ct);

    /// <summary>GET /peer/proxy/pump-status — get pump status from primary.</summary>
    Task<PeerProxyPumpStatusResponse?> GetPumpStatusAsync(string baseUrl, CancellationToken ct);
}

public sealed class PeerHttpClient : IPeerHttpClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICredentialStore _credentialStore;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly ILogger<PeerHttpClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public PeerHttpClient(
        IHttpClientFactory httpClientFactory,
        ICredentialStore credentialStore,
        IOptionsMonitor<AgentConfiguration> config,
        ILogger<PeerHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credentialStore = credentialStore;
        _config = config;
        _logger = logger;
    }

    public async Task<PeerHeartbeatResponse?> SendHeartbeatAsync(
        string baseUrl, PeerHeartbeatRequest request, CancellationToken ct)
    {
        return await PostAsync<PeerHeartbeatRequest, PeerHeartbeatResponse>(
            baseUrl, "/peer/heartbeat", request, ct);
    }

    public async Task<PeerHealthResponse?> GetHealthAsync(string baseUrl, CancellationToken ct)
    {
        return await GetAsync<PeerHealthResponse>(baseUrl, "/peer/health", ct);
    }

    public async Task<PeerLeadershipClaimResponse?> ClaimLeadershipAsync(
        string baseUrl, PeerLeadershipClaimRequest request, CancellationToken ct)
    {
        return await PostAsync<PeerLeadershipClaimRequest, PeerLeadershipClaimResponse>(
            baseUrl, "/peer/claim-leadership", request, ct);
    }

    public async Task<SnapshotPayload?> GetBootstrapAsync(string baseUrl, CancellationToken ct)
    {
        return await GetAsync<SnapshotPayload>(baseUrl, "/peer/bootstrap", ct);
    }

    public async Task<DeltaSyncPayload?> GetDeltaSyncAsync(
        string baseUrl, long sinceSeq, int limit, CancellationToken ct)
    {
        var path = $"/peer/sync?sinceSeq={sinceSeq}&limit={limit}";
        return await GetAsync<DeltaSyncPayload>(baseUrl, path, ct);
    }

    public async Task<PeerProxyPreAuthResponse?> ProxyPreAuthAsync(
        string baseUrl, PeerProxyPreAuthRequest request, CancellationToken ct)
    {
        return await PostAsync<PeerProxyPreAuthRequest, PeerProxyPreAuthResponse>(
            baseUrl, "/peer/proxy/preauth", request, ct);
    }

    public async Task<PeerProxyPumpStatusResponse?> GetPumpStatusAsync(string baseUrl, CancellationToken ct)
    {
        return await GetAsync<PeerProxyPumpStatusResponse>(baseUrl, "/peer/proxy/pump-status", ct);
    }

    private async Task<TResponse?> GetAsync<TResponse>(string baseUrl, string path, CancellationToken ct)
        where TResponse : class
    {
        try
        {
            var client = _httpClientFactory.CreateClient("peer");
            var url = $"{baseUrl.TrimEnd('/')}{path}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            await SignRequestAsync(request, "GET", path, null, ct);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Peer GET {Path} returned {StatusCode}", path, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Peer GET {Path} at {BaseUrl} failed", path, baseUrl);
            return null;
        }
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(
        string baseUrl, string path, TRequest body, CancellationToken ct)
        where TResponse : class
    {
        try
        {
            var client = _httpClientFactory.CreateClient("peer");
            var url = $"{baseUrl.TrimEnd('/')}{path}";

            var bodyJson = JsonSerializer.Serialize(body, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };
            await SignRequestAsync(request, "POST", path, bodyJson, ct);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Peer POST {Path} returned {StatusCode}", path, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Peer POST {Path} at {BaseUrl} failed", path, baseUrl);
            return null;
        }
    }

    private async Task SignRequestAsync(
        HttpRequestMessage request, string method, string path, string? body, CancellationToken ct)
    {
        var secret = await _credentialStore.GetSecretAsync(CredentialKeys.PeerSharedSecret, ct);
        if (string.IsNullOrEmpty(secret))
        {
            _logger.LogDebug("No peer shared secret configured — request will be unsigned");
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var signature = PeerHmacSigner.Sign(secret, method, path, timestamp, body);

        request.Headers.TryAddWithoutValidation("X-Peer-Signature", signature);
        request.Headers.TryAddWithoutValidation("X-Peer-Timestamp", timestamp);
    }
}
