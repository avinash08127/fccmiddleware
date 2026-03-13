using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FccDesktopAgent.Core.Adapter.Advatec;

/// <summary>
/// HTTP client for submitting Customer data to the Advatec EFD device.
/// POST http://{host}:{port}/api/v2/incoming
///
/// In Scenario C, the Customer endpoint triggers pump authorization and fiscal
/// receipt generation. The Advatec device is on localhost (no auth documented — AQ-6).
///
/// H-05: Accepts an externally-managed HttpClient (from IHttpClientFactory) instead of
/// creating a raw HttpClient internally. The caller owns the HttpClient lifecycle.
/// </summary>
public sealed class AdvatecApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly ILogger _logger;

    public AdvatecApiClient(
        HttpClient httpClient,
        string deviceAddress,
        int devicePort,
        ILogger logger)
    {
        _baseUrl = $"http://{deviceAddress}:{devicePort}";
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Submits Customer data to Advatec for pump authorization / fiscal receipt generation.
    /// Returns the raw HTTP response body for logging, and the status code.
    /// </summary>
    public async Task<AdvatecSubmitResult> SubmitCustomerDataAsync(
        AdvatecCustomerRequest request, CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/v2/incoming";
        var jsonBody = JsonSerializer.Serialize(request, JsonOpts);

        _logger.LogInformation(
            "Advatec: submitting Customer data to {Url} (Pump={Pump}, Dose={Dose}, CustIdType={CustIdType})",
            url, request.Data.Pump, request.Data.Dose, request.Data.CustIdType);

        try
        {
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content, ct);

            var responseBody = await response.Content.ReadAsStringAsync(ct);

            _logger.LogInformation(
                "Advatec: Customer submission response HTTP {StatusCode}: {Body}",
                (int)response.StatusCode, TruncateForLog(responseBody));

            return new AdvatecSubmitResult(
                Success: response.IsSuccessStatusCode,
                StatusCode: (int)response.StatusCode,
                ResponseBody: responseBody);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Advatec: Customer submission timed out after {Timeout}s", _httpClient.Timeout.TotalSeconds);
            return new AdvatecSubmitResult(
                Success: false,
                StatusCode: 0,
                ResponseBody: null,
                ErrorMessage: $"Timeout after {_httpClient.Timeout.TotalSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Advatec: Customer submission HTTP error: {Message}", ex.Message);
            return new AdvatecSubmitResult(
                Success: false,
                StatusCode: 0,
                ResponseBody: null,
                ErrorMessage: ex.Message);
        }
    }

    private static string TruncateForLog(string? s, int maxLen = 500)
        => s is null ? "(null)" : s.Length <= maxLen ? s : s[..maxLen] + "...";
}

/// <summary>
/// Result of submitting Customer data to the Advatec device.
/// </summary>
public sealed record AdvatecSubmitResult(
    bool Success,
    int StatusCode,
    string? ResponseBody,
    string? ErrorMessage = null);
