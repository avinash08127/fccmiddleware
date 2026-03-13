using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FccDesktopAgent.Core.Security;

/// <summary>
/// Validates that the cloud server's TLS certificate chain contains at least one
/// certificate whose SPKI (Subject Public Key Info) SHA-256 hash matches a known pin.
/// Mirrors the Android agent's OkHttp CertificatePinner behaviour.
///
/// T-DSK-015: Bootstrap (compiled-in) pins serve as the baseline. Additional pins
/// can be loaded from configuration via <see cref="LoadAdditionalPins"/> so that
/// emergency certificate rotations do not require a software update.
/// </summary>
public static class CertificatePinValidator
{
    // SHA-256 hashes of intermediate CA SubjectPublicKeyInfo — must match Android agent's AppModule.
    private static readonly HashSet<string> BootstrapPins = new(StringComparer.Ordinal)
    {
        "YLh1dUR9y6Kja30RrAn7JKnbQG/uEtLMkBgFF2Fuihg=", // Primary intermediate CA
        "Vjs8r4z+80wjNcr1YKepWQboSIRi63WsWXhIMN+eWys=", // Backup intermediate CA
    };

    // T-DSK-015: Additional pins loaded from configuration at startup.
    // Merged with bootstrap pins to form the effective pin set.
    private static volatile HashSet<string> _effectivePins = BootstrapPins;

    /// <summary>
    /// T-DSK-015: Merges additional SPKI SHA-256 Base64 pin hashes (from cloud config or
    /// appsettings) with the compiled-in bootstrap pins. Call once at startup or when
    /// config changes. Thread-safe via volatile reference swap.
    /// </summary>
    public static void LoadAdditionalPins(IEnumerable<string>? additionalPins)
    {
        if (additionalPins is null)
            return;

        var merged = new HashSet<string>(BootstrapPins, StringComparer.Ordinal);
        foreach (var pin in additionalPins)
        {
            if (!string.IsNullOrWhiteSpace(pin))
                merged.Add(pin.Trim());
        }

        _effectivePins = merged;
    }

    /// <summary>
    /// <see cref="HttpClientHandler.ServerCertificateCustomValidationCallback"/> delegate.
    /// Allows localhost/loopback without pinning (development), enforces pins for all other hosts.
    /// </summary>
    public static bool Validate(
        HttpRequestMessage requestMessage,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // Reject if the OS reports certificate errors (expired, name mismatch, etc.)
        if (sslPolicyErrors != SslPolicyErrors.None)
            return false;

        // Skip pinning for localhost/loopback (development only, matching CloudUrlGuard)
        var host = requestMessage.RequestUri?.Host;
        if (host is "localhost" or "127.0.0.1" or "[::1]" or "::1")
            return true;

        if (chain is null || certificate is null)
            return false;

        // Snapshot the effective pins for consistent evaluation across the chain.
        var pins = _effectivePins;

        // Check every certificate in the chain (leaf, intermediates, root) for a matching pin.
        foreach (var element in chain.ChainElements)
        {
            var spkiHash = ComputeSpkiSha256Base64(element.Certificate);
            if (pins.Contains(spkiHash))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Computes the Base64-encoded SHA-256 hash of a certificate's SubjectPublicKeyInfo (SPKI),
    /// the same format used by OkHttp CertificatePinner and HTTP Public Key Pinning (RFC 7469).
    /// </summary>
    internal static string ComputeSpkiSha256Base64(X509Certificate2 cert)
    {
        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        return Convert.ToBase64String(hash);
    }
}
