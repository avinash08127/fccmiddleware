using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using FccDesktopAgent.Core.Security;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Petronite;

/// <summary>
/// OAuth2 Client Credentials client for the Petronite API.
/// <para>
/// POST /oauth/token with Basic auth header (Base64(clientId:clientSecret)),
/// Content-Type: application/x-www-form-urlencoded, body: grant_type=client_credentials.
/// </para>
/// <para>
/// Caches the access token using the server-supplied <c>expires_in</c> TTL.
/// Proactively refreshes 60 seconds before expiry. Thread-safe via SemaphoreSlim.
/// Exposes <see cref="InvalidateTokenAsync"/> for 401 retry patterns.
/// </para>
/// </summary>
public sealed class PetroniteOAuthClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ProactiveRefreshBuffer = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpFactory;
    private readonly FccConnectionConfig _config;
    private readonly ICredentialStore? _credentialStore;
    private readonly ILogger<PetroniteOAuthClient> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public PetroniteOAuthClient(
        IHttpClientFactory httpFactory,
        FccConnectionConfig config,
        ILogger<PetroniteOAuthClient> logger,
        ICredentialStore? credentialStore = null)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;
        _credentialStore = credentialStore;
    }

    /// <summary>
    /// Returns a valid access token, fetching or refreshing as needed.
    /// Thread-safe: concurrent callers will wait on the semaphore while one caller refreshes.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        // Fast path: token is still valid (with proactive buffer).
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - ProactiveRefreshBuffer)
            return _cachedToken;

        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock — another thread may have refreshed.
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - ProactiveRefreshBuffer)
                return _cachedToken;

            var response = await RequestTokenAsync(ct);

            _cachedToken = response.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn);

            _logger.LogDebug(
                "Petronite OAuth token acquired (expires in {ExpiresIn}s)",
                response.ExpiresIn);

            return _cachedToken;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Invalidates the cached token so the next call to <see cref="GetAccessTokenAsync"/>
    /// will force a fresh token request. Use after receiving a 401 from the Petronite API.
    /// </summary>
    public async Task InvalidateTokenAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            _cachedToken = null;
            _tokenExpiresAt = DateTimeOffset.MinValue;
            _logger.LogDebug("Petronite OAuth token invalidated");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<PetroniteTokenResponse> RequestTokenAsync(CancellationToken ct)
    {
        var tokenEndpoint = _config.OAuthTokenEndpoint
            ?? throw new FccAdapterException(
                "Petronite OAuth token endpoint is not configured",
                isRecoverable: false);

        var clientId = _config.ClientId
            ?? throw new FccAdapterException(
                "Petronite OAuth client ID is not configured",
                isRecoverable: false);

        // S-DSK-010: Prefer credential store over plaintext config for client secret.
        var clientSecret = (_credentialStore is not null
                ? await _credentialStore.GetSecretAsync(CredentialKeys.PetroniteClientSecret, ct)
                : null)
            ?? _config.ClientSecret
            ?? throw new FccAdapterException(
                "Petronite OAuth client secret is not configured",
                isRecoverable: false);

        var http = _httpFactory.CreateClient("fcc");

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint);

        // Basic auth: Base64(clientId:clientSecret)
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        request.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
        });

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FccAdapterException(
                "Petronite OAuth token request transport failure",
                isRecoverable: true,
                ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var isRecoverable = statusCode is 408 or 429 || statusCode >= 500;

                _logger.LogWarning(
                    "Petronite OAuth token request failed: HTTP {StatusCode} — {Body}",
                    statusCode,
                    body);

                throw new FccAdapterException(
                    $"Petronite OAuth token request returned HTTP {statusCode}",
                    isRecoverable,
                    httpStatusCode: statusCode);
            }

            var tokenResponse = JsonSerializer.Deserialize<PetroniteTokenResponse>(body, JsonOptions);

            if (tokenResponse is null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                throw new FccAdapterException(
                    "Petronite OAuth token response was null or missing access_token",
                    isRecoverable: false);
            }

            return tokenResponse;
        }
    }
}
