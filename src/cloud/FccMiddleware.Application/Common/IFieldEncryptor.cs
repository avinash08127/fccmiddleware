namespace FccMiddleware.Application.Common;

/// <summary>
/// Encrypts and decrypts individual field values for at-rest protection of secrets
/// stored in the database (e.g., webhook secrets, client secrets, shared secrets).
/// Implementations must handle the case where a value was stored before encryption
/// was enabled (plaintext passthrough on read).
/// </summary>
public interface IFieldEncryptor
{
    /// <summary>Encrypts a plaintext value. Returns null for null input.</summary>
    string? Encrypt(string? plaintext);

    /// <summary>
    /// Decrypts a previously encrypted value. If the value is not in the expected
    /// encrypted format (legacy plaintext), it is returned as-is.
    /// Returns null for null input.
    /// </summary>
    string? Decrypt(string? ciphertext);
}
