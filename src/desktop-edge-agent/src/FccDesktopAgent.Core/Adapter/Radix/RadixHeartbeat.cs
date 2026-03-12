using System.Text;

namespace FccDesktopAgent.Core.Adapter.Radix;

/// <summary>
/// Radix heartbeat implementation using CMD_CODE=55 (product/price read).
///
/// There is no dedicated heartbeat endpoint in the Radix protocol.
/// CMD_CODE=55 on port P+1 is used as a liveness probe:
/// <list type="bullet">
///   <item>If FDC responds with product data, the FCC is alive</item>
///   <item>If request times out (5 seconds), the FCC is unreachable</item>
///   <item>Never throws — returns true/false only</item>
/// </list>
/// </summary>
public static class RadixHeartbeat
{
    /// <summary>Timeout for heartbeat probe.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Perform a heartbeat check against the Radix FDC.
    ///
    /// Sends a CMD_CODE=55 (product read) request to the transaction port (P+1)
    /// and returns <c>true</c> if any response is received within the 5-second timeout.
    /// Never throws — catches all exceptions and returns <c>false</c>.
    /// </summary>
    /// <param name="client">HttpClient instance for making the request.</param>
    /// <param name="transactionPortUrl">Base URL for the transaction port (P+1).</param>
    /// <param name="usnCode">Unique Station Number for the site.</param>
    /// <param name="sharedSecret">SHA-1 signing password.</param>
    /// <param name="token">Current token counter value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the FDC responded successfully within timeout, <c>false</c> otherwise.</returns>
    public static async Task<bool> CheckAsync(
        HttpClient client,
        string transactionPortUrl,
        int usnCode,
        string sharedSecret,
        int token,
        CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HeartbeatTimeout);

            var xmlBody = RadixXmlBuilder.BuildProductReadRequest(usnCode, token, sharedSecret);
            var headers = RadixXmlBuilder.BuildHttpHeaders(usnCode, RadixXmlBuilder.OperationProducts);

            using var request = new HttpRequestMessage(HttpMethod.Post, transactionPortUrl);
            request.Content = new StringContent(xmlBody, Encoding.UTF8, "Application/xml");

            foreach (var (key, value) in headers)
            {
                // Content-Type is set via StringContent; skip it to avoid duplicate header
                if (string.Equals(key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue;

                request.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
