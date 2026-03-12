using FccMiddleware.Domain.Models.Adapter;

namespace FccMiddleware.Application.Ingestion;

/// <summary>
/// Resolves site-specific FCC configuration for adapter factory input.
/// Joins FccConfig + Site + LegalEntity to produce a SiteFccConfig record.
/// </summary>
public interface ISiteFccConfigProvider
{
    /// <summary>
    /// Returns the SiteFccConfig and tenant LegalEntityId for the given site code.
    /// Returns null when no active FccConfig record exists for the site.
    /// Note: ApiKey is NOT populated here — it must be resolved from Secrets Manager
    /// when making outbound FCC calls. For ingest (push) normalization, ApiKey is irrelevant.
    /// </summary>
    Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetBySiteCodeAsync(
        string siteCode,
        CancellationToken ct = default);

    /// <summary>
    /// Looks up an active Radix FCC configuration by its USN-Code (Unique Station Number).
    /// Used for CLOUD_DIRECT Radix push ingestion where the FDC identifies itself via USN-Code header.
    /// Returns null when no active Radix FccConfig matches the given USN code.
    /// </summary>
    Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetByUsnCodeAsync(
        int usnCode,
        CancellationToken ct = default);

    /// <summary>
    /// Looks up an active Petronite FCC configuration by webhook secret.
    /// Used for Petronite webhook ingestion where the caller proves identity via X-Webhook-Secret.
    /// Returns null when no active Petronite FccConfig matches the given secret.
    /// </summary>
    Task<(SiteFccConfig Config, Guid LegalEntityId)?> GetByWebhookSecretAsync(
        string webhookSecret,
        CancellationToken ct = default);
}
