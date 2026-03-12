using FccMiddleware.Application.Common;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FccMiddleware.Infrastructure.Security;

/// <summary>
/// EF Core value converter that transparently encrypts on write and decrypts on read.
/// Applied to sensitive database columns (shared_secret, client_secret, webhook_secret, etc.).
/// </summary>
internal sealed class EncryptedFieldConverter : ValueConverter<string?, string?>
{
    public EncryptedFieldConverter(IFieldEncryptor encryptor)
        : base(
            plaintext => encryptor.Encrypt(plaintext),
            ciphertext => encryptor.Decrypt(ciphertext))
    {
    }
}
