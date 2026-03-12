using System.Collections.Frozen;
using System.Text.Json;
using FccDesktopAgent.Core.Adapter.Common;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Petronite;

/// <summary>
/// Resolves canonical pump/nozzle numbers to Petronite nozzle IDs and vice versa.
/// <para>
/// Fetches GET /nozzles/assigned and builds a bidirectional lookup.
/// Thread-safe: the snapshot is an immutable frozen dictionary swapped atomically.
/// Periodic refresh every 30 minutes; callers may force refresh via <see cref="RefreshAsync"/>.
/// </para>
/// </summary>
public sealed class PetroniteNozzleResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly IHttpClientFactory _httpFactory;
    private readonly FccConnectionConfig _config;
    private readonly PetroniteOAuthClient _oauthClient;
    private readonly ILogger<PetroniteNozzleResolver> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>NozzleId -> (PumpNumber, NozzleNumber)</summary>
    private FrozenDictionary<string, (int PumpNumber, int NozzleNumber)> _nozzleIdToCanonical =
        FrozenDictionary<string, (int, int)>.Empty;

    /// <summary>(PumpNumber, NozzleNumber) -> NozzleId</summary>
    private FrozenDictionary<(int PumpNumber, int NozzleNumber), string> _canonicalToNozzleId =
        FrozenDictionary<(int, int), string>.Empty;

    private DateTimeOffset _lastRefreshedAt = DateTimeOffset.MinValue;

    public PetroniteNozzleResolver(
        IHttpClientFactory httpFactory,
        FccConnectionConfig config,
        PetroniteOAuthClient oauthClient,
        ILogger<PetroniteNozzleResolver> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _oauthClient = oauthClient;
        _logger = logger;
    }

    /// <summary>
    /// Resolves canonical pump/nozzle numbers to a Petronite nozzle ID.
    /// Triggers a lazy refresh if the snapshot is stale.
    /// </summary>
    public async Task<string> ResolveNozzleIdAsync(int pumpNumber, int nozzleNumber, CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct);

        var key = (pumpNumber, nozzleNumber);
        if (_canonicalToNozzleId.TryGetValue(key, out var nozzleId))
            return nozzleId;

        throw new FccAdapterException(
            $"No Petronite nozzle mapping found for pump {pumpNumber} nozzle {nozzleNumber}",
            isRecoverable: false);
    }

    /// <summary>
    /// Resolves a Petronite nozzle ID to canonical pump/nozzle numbers.
    /// Triggers a lazy refresh if the snapshot is stale.
    /// </summary>
    public async Task<(int PumpNumber, int NozzleNumber)> ResolveCanonicalAsync(string nozzleId, CancellationToken ct = default)
    {
        await EnsureFreshAsync(ct);

        if (_nozzleIdToCanonical.TryGetValue(nozzleId, out var canonical))
            return canonical;

        throw new FccAdapterException(
            $"No canonical mapping found for Petronite nozzle ID '{nozzleId}'",
            isRecoverable: false);
    }

    /// <summary>
    /// Forces an immediate refresh of the nozzle assignment snapshot.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await LoadAssignmentsAsync(ct);
    }

    /// <summary>
    /// Returns the current list of nozzle assignments from the cached snapshot.
    /// </summary>
    public IReadOnlyDictionary<string, (int PumpNumber, int NozzleNumber)> GetCurrentSnapshot()
        => _nozzleIdToCanonical;

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task EnsureFreshAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastRefreshedAt < RefreshInterval)
            return;

        await LoadAssignmentsAsync(ct);
    }

    private async Task LoadAssignmentsAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock.
            if (DateTimeOffset.UtcNow - _lastRefreshedAt < TimeSpan.FromSeconds(5))
                return;

            var token = await _oauthClient.GetAccessTokenAsync(ct);

            var http = _httpFactory.CreateClient("fcc");
            var baseUri = new Uri(_config.BaseUrl.TrimEnd('/') + "/");
            var requestUri = new Uri(baseUri, "nozzles/assigned");

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new FccAdapterException(
                    "Petronite nozzle assignment fetch transport failure",
                    isRecoverable: true,
                    ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    var isRecoverable = statusCode is 408 or 429 || statusCode >= 500;

                    throw new FccAdapterException(
                        $"Petronite GET /nozzles/assigned returned HTTP {statusCode}",
                        isRecoverable,
                        httpStatusCode: statusCode);
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                var assignments = JsonSerializer.Deserialize<List<PetroniteNozzleAssignment>>(body, JsonOptions) ?? [];

                var nozzleIdToCanonical = new Dictionary<string, (int, int)>(assignments.Count);
                var canonicalToNozzleId = new Dictionary<(int, int), string>(assignments.Count);

                foreach (var a in assignments)
                {
                    nozzleIdToCanonical[a.NozzleId] = (a.PumpNumber, a.NozzleNumber);
                    canonicalToNozzleId[(a.PumpNumber, a.NozzleNumber)] = a.NozzleId;
                }

                _nozzleIdToCanonical = nozzleIdToCanonical.ToFrozenDictionary();
                _canonicalToNozzleId = canonicalToNozzleId.ToFrozenDictionary();
                _lastRefreshedAt = DateTimeOffset.UtcNow;

                _logger.LogInformation(
                    "Petronite nozzle resolver refreshed: {Count} nozzle(s) mapped",
                    assignments.Count);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
