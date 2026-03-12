using System.Security.Cryptography;
using System.Text;

namespace FccDesktopAgent.Core.Adapter.Radix;

/// <summary>
/// Radix SHA-1 message signing utility.
///
/// Every Radix FCC request and response is signed. The signature is a SHA-1 hash of
/// the relevant XML element content concatenated <b>immediately</b> with the shared secret
/// password — no separator, no trailing newline.
///
/// <b>Signing scope by port:</b>
/// <list type="bullet">
///   <item>
///     <b>Transaction management (port P+1):</b> <c>SHA1(&lt;REQ&gt;...&lt;/REQ&gt; + SECRET_PASSWORD)</c>.
///     The hash covers the full <c>&lt;REQ&gt;</c> element including its opening and closing tags.
///     Result is placed in the <c>&lt;SIGNATURE&gt;</c> sibling element inside <c>&lt;HOST_REQ&gt;</c>.
///   </item>
///   <item>
///     <b>External authorization / pre-auth (port P):</b> <c>SHA1(&lt;AUTH_DATA&gt;...&lt;/AUTH_DATA&gt; + SECRET_PASSWORD)</c>.
///     The hash covers the full <c>&lt;AUTH_DATA&gt;</c> element including its tags.
///     Result is placed in the <c>&lt;FDCSIGNATURE&gt;</c> sibling element inside <c>&lt;FDCMS&gt;</c>.
///   </item>
/// </list>
///
/// <b>Critical:</b> Whitespace and special characters in the XML content matter —
/// the hash must match character-for-character. Do NOT trim, normalize, or reformat
/// the XML before signing. The FDC validates signatures and returns RESP_CODE=251
/// (SIGNATURE_ERR) on mismatch.
/// </summary>
public static class RadixSignatureHelper
{
    /// <summary>
    /// Computes the SHA-1 signature for a transaction management request (port P+1).
    ///
    /// The input <paramref name="reqContent"/> must be the complete <c>&lt;REQ&gt;...&lt;/REQ&gt;</c>
    /// element as it will appear in the <c>&lt;HOST_REQ&gt;</c> XML body, including the opening
    /// <c>&lt;REQ&gt;</c> and closing <c>&lt;/REQ&gt;</c> tags. The <paramref name="sharedSecret"/>
    /// is concatenated immediately after <c>&lt;/REQ&gt;</c> with no separator.
    /// </summary>
    /// <param name="reqContent">Full <c>&lt;REQ&gt;...&lt;/REQ&gt;</c> XML element (tags included, not trimmed).</param>
    /// <param name="sharedSecret">The shared secret password configured for this FCC.</param>
    /// <returns>Lowercase hex SHA-1 hash (40 characters).</returns>
    public static string ComputeTransactionSignature(string reqContent, string sharedSecret)
    {
        var payload = reqContent + sharedSecret;
        return Sha1Hex(payload);
    }

    /// <summary>
    /// Computes the SHA-1 signature for an external authorization / pre-auth request (port P).
    ///
    /// The input <paramref name="authDataContent"/> must be the complete
    /// <c>&lt;AUTH_DATA&gt;...&lt;/AUTH_DATA&gt;</c> element as it will appear in the
    /// <c>&lt;FDCMS&gt;</c> XML body, including the opening <c>&lt;AUTH_DATA&gt;</c> and closing
    /// <c>&lt;/AUTH_DATA&gt;</c> tags. The <paramref name="sharedSecret"/> is concatenated
    /// immediately after <c>&lt;/AUTH_DATA&gt;</c> with no separator.
    /// </summary>
    /// <param name="authDataContent">Full <c>&lt;AUTH_DATA&gt;...&lt;/AUTH_DATA&gt;</c> XML element (tags included, not trimmed).</param>
    /// <param name="sharedSecret">The shared secret password configured for this FCC.</param>
    /// <returns>Lowercase hex SHA-1 hash (40 characters).</returns>
    public static string ComputeAuthSignature(string authDataContent, string sharedSecret)
    {
        var payload = authDataContent + sharedSecret;
        return Sha1Hex(payload);
    }

    /// <summary>
    /// Validates a signature received in an FDC response.
    ///
    /// Recomputes the SHA-1 hash of <paramref name="content"/> + <paramref name="sharedSecret"/>
    /// and compares it (case-insensitive) against the <paramref name="expectedSignature"/> from the response.
    ///
    /// For transaction responses (<c>&lt;FDC_RESP&gt;</c>), <paramref name="content"/> is the
    /// <c>&lt;TABLE&gt;...&lt;/TABLE&gt;</c> element.
    /// For pre-auth responses (<c>&lt;FDCMS&gt;</c>), <paramref name="content"/> is the
    /// <c>&lt;FDCACK&gt;...&lt;/FDCACK&gt;</c> element.
    /// </summary>
    /// <param name="content">The XML element content that was signed (tags included, not trimmed).</param>
    /// <param name="expectedSignature">The signature value from the response's SIGNATURE or FDCSIGNATURE element.</param>
    /// <param name="sharedSecret">The shared secret password configured for this FCC.</param>
    /// <returns><c>true</c> if the computed signature matches <paramref name="expectedSignature"/>, <c>false</c> otherwise.</returns>
    public static bool ValidateSignature(string content, string expectedSignature, string sharedSecret)
    {
        var computed = Sha1Hex(content + sharedSecret);
        return string.Equals(computed, expectedSignature, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Computes SHA-1 hash of <paramref name="input"/> and returns lowercase hex string (40 chars).
    ///
    /// The input is encoded as UTF-8 bytes before hashing. No trimming or normalization
    /// is performed — the caller is responsible for providing the exact content.
    /// </summary>
    private static string Sha1Hex(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = SHA1.HashData(inputBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
