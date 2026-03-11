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
}
