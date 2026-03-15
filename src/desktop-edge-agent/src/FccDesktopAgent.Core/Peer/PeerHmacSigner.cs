using System.Security.Cryptography;
using System.Text;

namespace FccDesktopAgent.Core.Peer;

/// <summary>
/// HMAC-SHA256 request signing for peer-to-peer authentication.
/// Both desktop (C#) and Android (Kotlin) implementations must produce
/// identical signatures for the same inputs.
/// </summary>
public static class PeerHmacSigner
{
    private static readonly TimeSpan MaxClockDrift = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Computes HMAC-SHA256 signature over the canonical request string.
    /// Format: "{method}\n{path}\n{timestamp}\n{bodyHash}"
    /// </summary>
    public static string Sign(string secret, string method, string path, string timestamp, string? body)
    {
        var bodyHash = ComputeBodyHash(body);
        var canonical = $"{method.ToUpperInvariant()}\n{path}\n{timestamp}\n{bodyHash}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies an incoming request's HMAC signature with clock drift tolerance.
    /// </summary>
    public static bool Verify(string secret, string method, string path, string timestamp, string? body, string signature)
    {
        // Validate timestamp drift
        if (!DateTimeOffset.TryParse(timestamp, out var requestTime))
            return false;

        var drift = DateTimeOffset.UtcNow - requestTime;
        if (drift.Duration() > MaxClockDrift)
            return false;

        var expected = Sign(secret, method, path, timestamp, body);

        // Constant-time comparison to prevent timing attacks
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }

    private static string ComputeBodyHash(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"; // SHA-256 of empty string

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
