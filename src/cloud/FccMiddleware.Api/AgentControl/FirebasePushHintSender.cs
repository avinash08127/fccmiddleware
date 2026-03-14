using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FccMiddleware.Domain.Enums;
using Microsoft.Extensions.Options;

namespace FccMiddleware.Api.AgentControl;

public sealed class FirebasePushHintSender : IAgentPushHintSender
{
    private const string FirebaseScope = "https://www.googleapis.com/auth/firebase.messaging";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly FirebaseMessagingOptions _options;
    private readonly ILogger<FirebasePushHintSender> _logger;

    public FirebasePushHintSender(
        HttpClient httpClient,
        IOptions<FirebaseMessagingOptions> options,
        ILogger<FirebasePushHintSender> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PushHintProviderResult> SendAsync(
        string registrationToken,
        PushHintRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ProjectId)
            || string.IsNullOrWhiteSpace(_options.ClientEmail)
            || string.IsNullOrWhiteSpace(_options.PrivateKey))
        {
            return new PushHintProviderResult(
                Succeeded: false,
                ErrorCode: "FCM_NOT_CONFIGURED",
                ErrorMessage: "Firebase service-account credentials are not configured.");
        }

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        if (accessToken is null)
        {
            return new PushHintProviderResult(
                Succeeded: false,
                ErrorCode: "FCM_AUTH_FAILED",
                ErrorMessage: "Could not obtain an FCM access token.");
        }

        var message = new
        {
            message = new
            {
                token = registrationToken,
                data = BuildData(request),
                android = new
                {
                    priority = "high"
                }
            }
        };

        using var requestMessage = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_options.BaseUrl.TrimEnd('/')}/v1/projects/{_options.ProjectId}/messages:send");
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        requestMessage.Content = JsonContent.Create(message, options: JsonOptions);

        using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "FCM send failed for device {DeviceId}. Status={StatusCode} Body={Body}",
                request.DeviceId,
                (int)response.StatusCode,
                Trim(responseText));

            return new PushHintProviderResult(
                Succeeded: false,
                ErrorCode: $"FCM_HTTP_{(int)response.StatusCode}",
                ErrorMessage: Trim(responseText));
        }

        using var json = JsonDocument.Parse(responseText);
        var providerMessageId = json.RootElement.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;

        return new PushHintProviderResult(
            Succeeded: true,
            ProviderMessageId: providerMessageId);
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var assertion = BuildJwtAssertion(now);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion
            })
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "FCM auth token request failed. Status={StatusCode} Body={Body}",
                (int)response.StatusCode,
                Trim(body));
            return null;
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.TryGetProperty("access_token", out var tokenElement)
            ? tokenElement.GetString()
            : null;
    }

    private string BuildJwtAssertion(DateTimeOffset now)
    {
        var headerJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT"
        }, JsonOptions);

        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["iss"] = _options.ClientEmail,
            ["scope"] = FirebaseScope,
            ["aud"] = _options.TokenEndpoint,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(55).ToUnixTimeSeconds()
        }, JsonOptions);

        var encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var unsignedToken = $"{encodedHeader}.{encodedPayload}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(NormalizePrivateKey(_options.PrivateKey!).ToCharArray());
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(unsignedToken),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{unsignedToken}.{Base64UrlEncode(signature)}";
    }

    private static Dictionary<string, string> BuildData(PushHintRequest request)
    {
        var data = new Dictionary<string, string>
        {
            ["kind"] = request.Kind switch
            {
                PushHintKind.COMMAND_PENDING => "command_pending",
                PushHintKind.CONFIG_CHANGED => "config_changed",
                _ => throw new ArgumentOutOfRangeException(nameof(request.Kind), request.Kind, "Unknown push hint kind.")
            },
            ["deviceId"] = request.DeviceId.ToString()
        };

        if (request.CommandCount.HasValue)
        {
            data["commandCount"] = request.CommandCount.Value.ToString();
        }

        if (request.ConfigVersion.HasValue)
        {
            data["configVersion"] = request.ConfigVersion.Value.ToString();
        }

        return data;
    }

    private static string NormalizePrivateKey(string privateKey) =>
        privateKey.Replace("\\n", "\n", StringComparison.Ordinal);

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Trim(string value) =>
        value.Length <= 1000 ? value : value[..1000];
}
