namespace FccDesktopAgent.Core.Security;

/// <summary>
/// Validates that cloud URLs use HTTPS.
/// DEA-6.2: All cloud communication must use TLS.
/// </summary>
public static class CloudUrlGuard
{
    /// <summary>
    /// Validates that the given cloud URL uses HTTPS.
    /// Allows HTTP only for localhost/loopback addresses (development).
    /// </summary>
    /// <returns>true if the URL is safe for cloud communication.</returns>
    public static bool IsSecure(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            return true;

        // Allow HTTP for local development only (localhost, IPv4 loopback, IPv6 loopback)
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host == "127.0.0.1"
                || uri.Host == "[::1]"
                || uri.Host == "::1"))
            return true;

        return false;
    }
}
