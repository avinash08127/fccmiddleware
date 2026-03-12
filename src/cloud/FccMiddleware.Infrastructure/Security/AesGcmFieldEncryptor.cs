using System.Security.Cryptography;
using FccMiddleware.Application.Common;
using Microsoft.Extensions.Logging;

namespace FccMiddleware.Infrastructure.Security;

/// <summary>
/// AES-256-GCM field-level encryptor for database secrets.
///
/// Encrypted values are stored as: <c>$aes256gcm$v1${base64(nonce|ciphertext|tag)}</c>
/// The prefix allows the decryptor to distinguish encrypted values from legacy plaintext,
/// enabling incremental migration without a bulk data update.
///
/// Key material is provided via configuration (FieldEncryption:Key, 64 hex chars = 32 bytes).
/// </summary>
public sealed class AesGcmFieldEncryptor : IFieldEncryptor
{
    internal const string Prefix = "$aes256gcm$v1$";
    private const int NonceSize = 12; // AES-GCM standard
    private const int TagSize = 16;   // AES-GCM standard

    private readonly byte[] _key;
    private readonly ILogger<AesGcmFieldEncryptor> _logger;

    public AesGcmFieldEncryptor(byte[] key, ILogger<AesGcmFieldEncryptor> logger)
    {
        if (key.Length != 32)
            throw new ArgumentException("AES-256 key must be exactly 32 bytes.", nameof(key));
        _key = key;
        _logger = logger;
    }

    public string? Encrypt(string? plaintext)
    {
        if (plaintext is null) return null;

        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Pack as nonce || ciphertext || tag
        var packed = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(packed, 0);
        ciphertext.CopyTo(packed, NonceSize);
        tag.CopyTo(packed, NonceSize + ciphertext.Length);

        return Prefix + Convert.ToBase64String(packed);
    }

    public string? Decrypt(string? value)
    {
        if (value is null) return null;

        // Legacy plaintext: return as-is if not prefixed
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            return value;

        try
        {
            var packed = Convert.FromBase64String(value[Prefix.Length..]);
            if (packed.Length < NonceSize + TagSize)
                throw new CryptographicException("Encrypted payload too short.");

            var nonce = packed.AsSpan(0, NonceSize);
            var ciphertextLength = packed.Length - NonceSize - TagSize;
            var ciphertext = packed.AsSpan(NonceSize, ciphertextLength);
            var tag = packed.AsSpan(NonceSize + ciphertextLength, TagSize);

            var plaintext = new byte[ciphertextLength];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt field value — returning raw value as fallback");
            return value;
        }
    }
}
