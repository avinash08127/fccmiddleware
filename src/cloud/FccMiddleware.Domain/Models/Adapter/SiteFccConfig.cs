using FccMiddleware.Domain.Enums;
using FccMiddleware.Domain.Common;

namespace FccMiddleware.Domain.Models.Adapter;

/// <summary>
/// Minimum config inputs required by IFccAdapterFactory.Resolve and the adapter implementations.
/// Sourced from the FccConfig entity + site master data. The ApiKey value must be resolved
/// from AWS Secrets Manager before constructing this record — never stored directly.
/// </summary>
public sealed record SiteFccConfig
{
    /// <summary>Site identifier — used as an adapter context key.</summary>
    public required string SiteCode { get; init; }

    /// <summary>FCC vendor. Must match the adapter being resolved.</summary>
    public required FccVendor FccVendor { get; init; }

    /// <summary>Connection protocol for the FCC endpoint.</summary>
    public required ConnectionProtocol ConnectionProtocol { get; init; }

    /// <summary>FCC host address (IP or hostname).</summary>
    public required string HostAddress { get; init; }

    /// <summary>FCC port number.</summary>
    public required int Port { get; init; }

    /// <summary>
    /// API key used in the X-API-Key header. Must be resolved from Secrets Manager
    /// (FccConfig.CredentialRef) before calling Resolve — never the raw credential ref.
    /// </summary>
    [Sensitive]
    public required string ApiKey { get; init; }

    /// <summary>How transactions flow from the FCC to the middleware.</summary>
    public required IngestionMethod IngestionMethod { get; init; }

    /// <summary>Pull interval in seconds. Required when IngestionMethod is PULL or HYBRID.</summary>
    public int? PullIntervalSeconds { get; init; }

    /// <summary>ISO 4217 currency code for monetary field conversions.</summary>
    public required string CurrencyCode { get; init; }

    /// <summary>IANA timezone identifier for the site (e.g., "Africa/Blantyre").</summary>
    public required string Timezone { get; init; }

    /// <summary>
    /// Offset subtracted from FCC pump numbers to produce canonical pump numbers.
    /// Zero means FCC pump numbers are used directly.
    /// </summary>
    public int PumpNumberOffset { get; init; } = 0;

    /// <summary>
    /// Maps raw FCC product codes to canonical codes (e.g., {"01" → "PMS", "02" → "AGO"}).
    /// When a code is absent the raw FCC code is preserved as-is.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProductCodeMapping { get; init; } =
        new Dictionary<string, string>();
}
