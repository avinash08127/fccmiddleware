namespace FccMiddleware.Application.Registration;

/// <summary>
/// Generates device JWTs for Edge Agent authentication.
/// Implementation lives in the API layer (has access to signing keys).
/// </summary>
public interface IDeviceTokenService
{
    /// <summary>
    /// Creates a signed device JWT with the standard Edge Agent claims.
    /// </summary>
    (string Token, DateTimeOffset ExpiresAt) GenerateDeviceToken(
        Guid deviceId, string siteCode, Guid legalEntityId);
}
