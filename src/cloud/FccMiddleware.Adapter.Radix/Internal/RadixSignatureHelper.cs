using System.Security.Cryptography;
using System.Text;

namespace FccMiddleware.Adapter.Radix.Internal;

/// <summary>
/// SHA-1 signing utility for Radix FDC protocol message authentication.
/// Computes and validates HMAC-like signatures: SHA1(xmlContent + sharedSecret).
/// </summary>
internal static class RadixSignatureHelper
{
    /// <summary>Compute SHA-1 signature for transaction-port messages (port P+1).</summary>
    internal static string ComputeTransactionSignature(string xmlContent, string sharedSecret)
    {
        return ComputeSha1(xmlContent + sharedSecret);
    }

    /// <summary>Compute SHA-1 signature for auth-port messages (port P).</summary>
    internal static string ComputeAuthSignature(string xmlContent, string sharedSecret)
    {
        return ComputeSha1(xmlContent + sharedSecret);
    }

    /// <summary>Validate a received signature against expected using constant-time comparison.</summary>
    internal static bool ValidateSignature(string receivedSignature, string xmlContent, string sharedSecret)
    {
        var expected = ComputeSha1(xmlContent + sharedSecret);

        // Use constant-time comparison to prevent timing side-channel attacks.
        // SHA-1 is retained for vendor protocol compatibility (Radix FDC spec).
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var receivedBytes = Encoding.UTF8.GetBytes(receivedSignature.ToLowerInvariant());

        return CryptographicOperations.FixedTimeEquals(expectedBytes, receivedBytes);
    }

    private static string ComputeSha1(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
